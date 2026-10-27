using System;
using System.ComponentModel;
using FluentAssertions;
using LightBulb.PlatformInterop;
using LightBulb.PlatformInterop.Internal;
using Xunit;

namespace LightBulb.Core.Tests;

public class DeviceContextSpecs
{
    [Fact]
    public void I_detect_external_gamma_resets_even_when_the_requested_tint_has_not_changed()
    {
        var api = new FakeGammaApi();
        using var context = new DeviceContext(1, api);
        context.SetGamma(0.85, 0.6, 0.3);
        context.IsGammaCurrent().Should().BeTrue();

        using (var external = new DeviceContext(2, api))
            external.ResetGamma();
        context.IsGammaCurrent().Should().BeFalse();
        context.SetGamma(0.85, 0.6, 0.3);
        context.IsGammaCurrent().Should().BeTrue();

        using (var delayedReset = new DeviceContext(2, api))
            delayedReset.ResetGamma();
        context.IsGammaCurrent().Should().BeFalse();
    }

    [Fact]
    public void I_do_not_mistake_lower_precision_driver_LUTs_for_a_gamma_reset()
    {
        var api = new FakeGammaApi { Quantize = true };
        using var context = new DeviceContext(1, api);
        context.SetGamma(0.85, 0.6, 0.3);
        context.IsGammaCurrent().Should().BeTrue();
    }

    [Fact]
    public void I_report_failed_gamma_reads_and_writes_instead_of_recording_success()
    {
        var api = new FakeGammaApi();
        using var context = new DeviceContext(1, api);
        context.SetGamma(0.85, 0.6, 0.3);
        api.FailWrite = true;
        Action write = () => context.SetGamma(0.8, 0.5, 0.2);
        write.Should().Throw<Win32Exception>();
        context.IsGammaCurrent().Should().BeTrue();

        api.FailRead = true;
        Action read = () => context.IsGammaCurrent();
        read.Should().Throw<Win32Exception>();
    }

    private sealed class FakeGammaApi : IDeviceGammaApi
    {
        private GammaRamp _ramp;
        public bool Quantize { get; set; }
        public bool FailRead { get; set; }
        public bool FailWrite { get; set; }

        public GammaRamp GetGammaRamp(nint handle) =>
            !FailRead ? _ramp : throw new Win32Exception(5);

        public void SetGammaRamp(nint handle, GammaRamp ramp)
        {
            if (FailWrite)
                throw new Win32Exception(5);
            _ramp = new()
            {
                Red = Copy(ramp.Red),
                Green = Copy(ramp.Green),
                Blue = Copy(ramp.Blue),
            };
        }

        private ushort[] Copy(ushort[] values)
        {
            var copy = (ushort[])values.Clone();
            if (Quantize)
                for (var i = 0; i < copy.Length; i++)
                    copy[i] = (ushort)(copy[i] & 0xFF00);
            return copy;
        }

        public void DeleteDC(nint handle) { }
    }
}
