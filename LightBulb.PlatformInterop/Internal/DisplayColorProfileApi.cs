using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LightBulb.PlatformInterop.Internal;

internal interface IDisplayColorProfileApi
{
    uint GetScope(DisplayDevice display);
    string? GetDefault(DisplayDevice display, uint scope);
    byte[] ReadProfile(string profileName);
    bool IsInstalled(string profileName);
    void SetUserScope(DisplayDevice display, bool enabled);
    void Install(string profilePath);
    void AddAssociation(DisplayDevice display, string profileName);
    void SetDefault(DisplayDevice display, string? profileName);
    void RemoveAssociation(DisplayDevice display, string profileName);
    void Uninstall(string profileName);
}

internal sealed class DisplayColorProfileApi : IDisplayColorProfileApi
{
    private readonly string _colorDirectory = GetColorDirectory();

    private static string GetColorDirectory()
    {
        uint size = 0;
        if (
            !NativeMethods.GetColorDirectoryW(null, null, ref size)
            && Marshal.GetLastWin32Error() != 122
        )
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not locate the Windows color-profile directory."
            );
        var directory = new StringBuilder(checked((int)size / 2));
        if (!NativeMethods.GetColorDirectoryW(null, directory, ref size))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not read the Windows color-profile directory."
            );
        return directory.ToString();
    }

    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    private static bool IsMissing(int result) =>
        result
            is unchecked((int)0x80070002)
                or unchecked((int)0x80070490)
                or unchecked((int)0x800707DF);

    public uint GetScope(DisplayDevice display)
    {
        Check(
            NativeMethods.ColorProfileGetDisplayUserScope(
                Luid.FromInt64(display.AdapterId),
                display.SourceId,
                out var scope
            )
        );
        return scope;
    }

    public string? GetDefault(DisplayDevice display, uint scope)
    {
        var result = NativeMethods.ColorProfileGetDisplayDefault(
            scope,
            Luid.FromInt64(display.AdapterId),
            display.SourceId,
            0,
            8,
            out var pointer
        );
        try
        {
            if (IsMissing(result))
                return null;
            Check(result);
            return pointer == 0 ? null : Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            if (pointer != 0)
                NativeMethods.LocalFree(pointer);
        }
    }

    public byte[] ReadProfile(string profileName) =>
        File.ReadAllBytes(Path.Combine(_colorDirectory, Path.GetFileName(profileName)));

    public bool IsInstalled(string profileName) =>
        File.Exists(Path.Combine(_colorDirectory, profileName));

    public void SetUserScope(DisplayDevice display, bool enabled)
    {
        if (!NativeMethods.WcsSetUsePerUserProfiles(display.DeviceKey, 0x6D6E7472, enabled))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not change the monitor's per-user profile scope."
            );
    }

    public void Install(string profilePath)
    {
        if (!NativeMethods.InstallColorProfileW(null, profilePath))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not install the temporary HDR profile. Windows may require administrator privileges."
            );
    }

    public void AddAssociation(DisplayDevice display, string profileName) =>
        Check(
            NativeMethods.ColorProfileAddDisplayAssociation(
                1,
                profileName,
                Luid.FromInt64(display.AdapterId),
                display.SourceId,
                false,
                true
            )
        );

    public void SetDefault(DisplayDevice display, string? profileName)
    {
        if (profileName is not null)
        {
            Check(
                NativeMethods.ColorProfileSetDisplayDefaultAssociation(
                    1,
                    profileName,
                    0,
                    8,
                    Luid.FromInt64(display.AdapterId),
                    display.SourceId
                )
            );
        }
        else if (!NativeMethods.WcsSetDefaultColorProfile(1, display.DeviceKey, 0, 8, 0, null))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not clear the temporary HDR default profile."
            );
        }
    }

    public void RemoveAssociation(DisplayDevice display, string profileName)
    {
        var result = NativeMethods.ColorProfileRemoveDisplayAssociation(
            1,
            profileName,
            Luid.FromInt64(display.AdapterId),
            display.SourceId,
            true
        );
        if (!IsMissing(result))
            Check(result);
    }

    public void Uninstall(string profileName)
    {
        if (!NativeMethods.UninstallColorProfileW(null, profileName, true))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not remove a temporary HDR profile; its recovery record has been retained."
            );
    }
}
