using System.Runtime.InteropServices;
using System.Text;

namespace LightBulb.PlatformInterop.Internal;

internal static partial class NativeMethods
{
    private const string Mscms = "mscms.dll";

    [DllImport(Mscms, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool InstallColorProfileW(string? machineName, string profilePath);

    [DllImport(Mscms, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool UninstallColorProfileW(
        string? machineName,
        string profileName,
        bool delete
    );

    [DllImport(Mscms)]
    public static extern int ColorProfileGetDisplayUserScope(
        Luid adapterId,
        uint sourceId,
        out uint scope
    );

    [DllImport(Mscms)]
    public static extern int ColorProfileGetDeviceCapabilities(
        uint scope,
        Luid adapterId,
        uint sourceId,
        uint capabilitiesType,
        ref Mhc2Capabilities capabilities
    );

    [DllImport(Mscms, CharSet = CharSet.Unicode)]
    public static extern int ColorProfileGetDisplayDefault(
        uint scope,
        Luid adapterId,
        uint sourceId,
        uint profileType,
        uint profileSubType,
        out nint profileName
    );

    [DllImport(Mscms, CharSet = CharSet.Unicode)]
    public static extern int ColorProfileAddDisplayAssociation(
        uint scope,
        string profileName,
        Luid adapterId,
        uint sourceId,
        bool setAsDefault,
        bool advancedColor
    );

    [DllImport(Mscms, CharSet = CharSet.Unicode)]
    public static extern int ColorProfileSetDisplayDefaultAssociation(
        uint scope,
        string? profileName,
        uint profileType,
        uint profileSubType,
        Luid adapterId,
        uint sourceId
    );

    [DllImport(Mscms, CharSet = CharSet.Unicode)]
    public static extern int ColorProfileRemoveDisplayAssociation(
        uint scope,
        string profileName,
        Luid adapterId,
        uint sourceId,
        bool advancedColor
    );

    [DllImport(Mscms, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool WcsSetUsePerUserProfiles(
        string deviceKey,
        uint deviceClass,
        bool usePerUserProfiles
    );

    [DllImport(Mscms, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool WcsSetDefaultColorProfile(
        uint scope,
        string deviceKey,
        uint profileType,
        uint profileSubType,
        uint profileId,
        string? profileName
    );

    [DllImport(Mscms, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetColorDirectoryW(
        string? machineName,
        StringBuilder? directory,
        ref uint size
    );

    [DllImport(Kernel32)]
    public static extern nint LocalFree(nint memory);
}
