using System.Runtime.InteropServices;

namespace LightBulb.PlatformInterop.Internal;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct Luid(uint LowPart, int HighPart)
{
    public static Luid FromInt64(long value) => new((uint)value, (int)(value >> 32));

    public long ToInt64() => (long)HighPart << 32 | LowPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigSource
{
    public Luid AdapterId;
    public uint Id;
    public uint ModeInfoIndex;
    public uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigTarget
{
    public Luid AdapterId;
    public uint Id;
    public uint ModeInfoIndex;
    public uint OutputTechnology;
    public uint Rotation;
    public uint Scaling;
    public uint RefreshNumerator;
    public uint RefreshDenominator;
    public uint ScanLineOrdering;
    public int IsAvailable;
    public uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPath
{
    public DisplayConfigSource Source;
    public DisplayConfigTarget Target;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigHeader(uint type, uint size, Luid adapterId, uint id)
{
    public uint Type = type;
    public uint Size = size;
    public Luid AdapterId = adapterId;
    public uint Id = id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DisplayConfigSourceName
{
    public DisplayConfigHeader Header;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DisplayConfigTargetName
{
    public DisplayConfigHeader Header;
    public uint Flags;
    public uint OutputTechnology;
    public ushort Manufacturer;
    public ushort Product;
    public uint ConnectorInstance;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string FriendlyName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string DevicePath;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigAdvancedColor
{
    public DisplayConfigHeader Header;
    public uint Flags;
    public uint ColorEncoding;
    public uint BitsPerChannel;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DisplayDeviceInfo
{
    public uint Size;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string Description;

    public uint StateFlags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string DeviceId;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string DeviceKey;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Mhc2Capabilities
{
    public uint Size;
    public int IsSupported;
    public uint LutEntryCount;
    public uint MatrixRows;
    public uint MatrixColumns;
}
