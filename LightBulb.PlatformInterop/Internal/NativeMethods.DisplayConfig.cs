using System;
using System.Runtime.InteropServices;

namespace LightBulb.PlatformInterop.Internal;

internal static partial class NativeMethods
{
    [DllImport(User32)]
    public static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint pathCount,
        out uint modeCount
    );

    [DllImport(User32)]
    public static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathCount,
        [Out] DisplayConfigPath[] paths,
        ref uint modeCount,
        nint modes,
        nint topologyId
    );

    [DllImport(User32, EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)]
    public static extern int GetDisplaySourceName(ref DisplayConfigSourceName request);

    [DllImport(User32, EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)]
    public static extern int GetDisplayTargetName(ref DisplayConfigTargetName request);

    [DllImport(User32, EntryPoint = "DisplayConfigGetDeviceInfo")]
    public static extern int GetDisplayAdvancedColor(ref DisplayConfigAdvancedColor request);

    [DllImport(
        User32,
        EntryPoint = "EnumDisplayDevicesW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    public static extern bool EnumDisplayDevices(
        string deviceName,
        uint index,
        ref DisplayDeviceInfo device,
        uint flags
    );
}
