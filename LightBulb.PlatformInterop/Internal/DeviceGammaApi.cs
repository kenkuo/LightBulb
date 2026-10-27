using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LightBulb.PlatformInterop.Internal;

internal interface IDeviceGammaApi
{
    GammaRamp GetGammaRamp(nint handle);
    void SetGammaRamp(nint handle, GammaRamp ramp);
    void DeleteDC(nint handle);
}

internal sealed class DeviceGammaApi : IDeviceGammaApi
{
    public GammaRamp GetGammaRamp(nint handle)
    {
        if (!NativeMethods.GetDeviceGammaRamp(handle, out var ramp))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not read the monitor's gamma ramp after an HDR profile change."
            );
        return ramp;
    }

    public void SetGammaRamp(nint handle, GammaRamp ramp)
    {
        if (!NativeMethods.SetDeviceGammaRamp(handle, ref ramp))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not apply the monitor's gamma ramp."
            );
    }

    public void DeleteDC(nint handle)
    {
        if (!NativeMethods.DeleteDC(handle))
            Debug.WriteLine(
                $"Failed to dispose device context #{handle}. Error {Marshal.GetLastWin32Error()}."
            );
    }
}
