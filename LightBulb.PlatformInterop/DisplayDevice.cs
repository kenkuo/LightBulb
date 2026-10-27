using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using LightBulb.Core;
using LightBulb.PlatformInterop.Internal;
using Vortice.DXGI;

namespace LightBulb.PlatformInterop;

public sealed record DisplayDevice(
    string Id,
    string DeviceName,
    string FriendlyName,
    string DeviceKey,
    long AdapterId,
    uint SourceId,
    bool IsHdrEnabled,
    bool IsMirrored,
    bool SupportsMhc2,
    HdrDisplayMetadata? HdrMetadata
)
{
    public static IReadOnlyList<DisplayDevice> GetAll()
    {
        const uint activePaths = 2;
        DisplayConfigPath[] paths;
        uint pathCount;

        for (var attempt = 0; ; attempt++)
        {
            var result = NativeMethods.GetDisplayConfigBufferSizes(
                activePaths,
                out pathCount,
                out var modeCount
            );
            if (result != 0)
                throw new Win32Exception(result, "Failed to enumerate display configuration.");

            paths = new DisplayConfigPath[pathCount];
            var modes = Marshal.AllocHGlobal(checked((int)modeCount * 64));
            try
            {
                result = NativeMethods.QueryDisplayConfig(
                    activePaths,
                    ref pathCount,
                    paths,
                    ref modeCount,
                    modes,
                    0
                );
            }
            finally
            {
                Marshal.FreeHGlobal(modes);
            }

            if (result == 122 && attempt < 3)
                continue;
            if (result != 0)
                throw new Win32Exception(result, "Failed to query active display configuration.");
            break;
        }

        var outputs = GetOutputDescriptions();
        var displays = new List<DisplayDevice>();
        foreach (var path in paths.Take((int)pathCount))
        {
            var source = new DisplayConfigSourceName
            {
                Header = new(
                    1,
                    (uint)Marshal.SizeOf<DisplayConfigSourceName>(),
                    path.Source.AdapterId,
                    path.Source.Id
                ),
            };
            var result = NativeMethods.GetDisplaySourceName(ref source);
            if (result != 0)
                throw new Win32Exception(result, "Failed to read the display source name.");

            var target = new DisplayConfigTargetName
            {
                Header = new(
                    2,
                    (uint)Marshal.SizeOf<DisplayConfigTargetName>(),
                    path.Target.AdapterId,
                    path.Target.Id
                ),
            };
            result = NativeMethods.GetDisplayTargetName(ref target);
            if (result != 0)
                throw new Win32Exception(result, "Failed to read the monitor identity.");

            var device = new DisplayDeviceInfo { Size = (uint)Marshal.SizeOf<DisplayDeviceInfo>() };
            if (!NativeMethods.EnumDisplayDevices(source.DeviceName, 0, ref device, 0))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Failed to read the monitor device key."
                );

            outputs.TryGetValue(source.DeviceName, out var output);
            var advancedColor = new DisplayConfigAdvancedColor
            {
                Header = new(
                    9,
                    (uint)Marshal.SizeOf<DisplayConfigAdvancedColor>(),
                    path.Target.AdapterId,
                    path.Target.Id
                ),
            };
            result = NativeMethods.GetDisplayAdvancedColor(ref advancedColor);
            if (result != 0)
                Debug.WriteLine(
                    $"Could not query advanced color for {source.DeviceName}: {result}."
                );

            var isHdr = output is not null
                ? output.Value.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020
                : result == 0 && (advancedColor.Flags & 2) != 0;
            var capabilities = new Mhc2Capabilities
            {
                Size = (uint)Marshal.SizeOf<Mhc2Capabilities>(),
            };
            if (isHdr && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            {
                try
                {
                    result = NativeMethods.ColorProfileGetDeviceCapabilities(
                        1,
                        path.Target.AdapterId,
                        path.Source.Id,
                        2,
                        ref capabilities
                    );
                    if (result < 0)
                        Debug.WriteLine(
                            $"Could not query MHC2 support for {source.DeviceName}: 0x{result:X8}."
                        );
                }
                catch (EntryPointNotFoundException ex)
                {
                    Debug.WriteLine(
                        $"Windows does not expose MHC2 capabilities for {source.DeviceName}: {ex.Message}"
                    );
                }
            }

            var metadata = output is { } description
                ? new HdrDisplayMetadata(
                    description.RedPrimary[0],
                    description.RedPrimary[1],
                    description.GreenPrimary[0],
                    description.GreenPrimary[1],
                    description.BluePrimary[0],
                    description.BluePrimary[1],
                    description.WhitePoint[0],
                    description.WhitePoint[1],
                    description.MinLuminance,
                    description.MaxLuminance,
                    description.MaxFullFrameLuminance
                )
                : null;

            displays.Add(
                new(
                    string.IsNullOrWhiteSpace(target.DevicePath)
                        ? source.DeviceName
                        : target.DevicePath,
                    source.DeviceName,
                    string.IsNullOrWhiteSpace(target.FriendlyName)
                        ? device.Description
                        : target.FriendlyName,
                    device.DeviceKey,
                    path.Target.AdapterId.ToInt64(),
                    path.Source.Id,
                    isHdr,
                    false,
                    capabilities.IsSupported != 0
                        && capabilities.MatrixRows >= 3
                        && capabilities.MatrixColumns >= 3,
                    metadata
                )
            );
        }

        return displays
            .Select(d =>
                d with
                {
                    IsMirrored = displays.Count(other => other.DeviceName == d.DeviceName) > 1,
                }
            )
            .ToArray();
    }

    private static IReadOnlyDictionary<string, OutputDescription1?> GetOutputDescriptions()
    {
        var outputs = new Dictionary<string, OutputDescription1?>(StringComparer.OrdinalIgnoreCase);
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var result = factory.EnumAdapters1(adapterIndex, out var adapter);
            if (result == ResultCode.NotFound)
                break;
            result.CheckError();
            using (adapter)
            {
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    result = adapter.EnumOutputs(outputIndex, out var output);
                    if (result == ResultCode.NotFound)
                        break;
                    result.CheckError();
                    using (output)
                    using (var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>())
                    {
                        if (output6 is not null)
                        {
                            var description = output6.Description1;
                            outputs[description.DeviceName] = description;
                        }
                    }
                }
            }
        }

        return outputs;
    }
}
