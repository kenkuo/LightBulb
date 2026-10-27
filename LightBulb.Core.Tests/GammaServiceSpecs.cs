using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using FluentAssertions;
using LightBulb.PlatformInterop;
using LightBulb.PlatformInterop.Internal;
using LightBulb.Services;
using Xunit;

namespace LightBulb.Core.Tests;

public class GammaServiceSpecs
{
    private static readonly ColorConfiguration Tint = new(4000, 0.9);
    private static readonly ColorConfiguration WarmerTint = new(3500, 0.8);

    [Fact]
    public void I_retry_a_failed_SDR_write_without_opening_settings_or_enabling_polling()
    {
        using var fixture = new Fixture();
        fixture.Update();
        fixture.Api.FailWrites = true;
        fixture.Update(WarmerTint);
        fixture.Service.ErrorMessage.Should().Contain("Injected gamma write failure");
        fixture.Api.FailWrites = false;
        fixture.Api.InvalidHandles.Add(fixture.LastHandle);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Update(WarmerTint);

        fixture.Service.ErrorMessage.Should().BeNull();
        fixture.ContextCreations.Should().Be(2);
        fixture.Api.DeletedHandles.Should().ContainSingle();
        fixture.Api.SuccessfulWrites.Should().Be(2);
        fixture.Api.Ramp.Blue[200].Should().BeLessThan(fixture.Api.Ramp.Red[200]);
    }

    [Fact]
    public void I_reapply_an_unchanged_target_after_a_transient_gamma_read_failure()
    {
        using var fixture = new Fixture();
        fixture.Update();
        fixture.Api.FailReads = true;
        fixture.Update();
        fixture.Service.ErrorMessage.Should().Contain("Injected gamma read failure");
        fixture.Api.FailReads = false;

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Update();
        fixture.Update();

        fixture.Service.ErrorMessage.Should().BeNull();
        fixture.Api.SuccessfulWrites.Should().Be(2);
        fixture.ContextCreations.Should().Be(2);
    }

    [Fact]
    public void I_limit_repeated_failures_to_one_attempt_per_second()
    {
        using var fixture = new Fixture();
        fixture.Api.FailWrites = true;
        fixture.Update();
        for (var i = 0; i < 20; i++)
            fixture.Update();
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(999));
        fixture.Update();
        fixture.Api.WriteAttempts.Should().Be(1);
        fixture.ContextCreations.Should().Be(1);

        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        fixture.Update();
        fixture.Api.WriteAttempts.Should().Be(2);
        fixture.Service.ErrorMessage.Should().NotBeNull();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Update();
        fixture.Api.WriteAttempts.Should().Be(3);
    }

    [Fact]
    public void I_recover_when_a_display_context_could_not_initially_be_created()
    {
        using var fixture = new Fixture(missingContexts: 2);
        fixture.Update();
        fixture.Service.ErrorMessage.Should().Contain("Could not create a gamma device context");

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Update();

        fixture.Service.ErrorMessage.Should().BeNull();
        fixture.ContextCreations.Should().Be(3);
        fixture.Api.SuccessfulWrites.Should().Be(1);
    }

    [Fact]
    public void I_retry_an_excluded_monitors_failed_reset_without_restoring_its_tint()
    {
        using var fixture = new Fixture();
        fixture.Update();
        fixture.Api.FailWrites = true;
        fixture.Settings.SetMonitorConfiguration(fixture.Display.Id, new() { IsEnabled = false });
        fixture.Update();
        fixture.Service.ErrorMessage.Should().NotBeNull();
        fixture.Api.FailWrites = false;

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Update();
        var writes = fixture.Api.SuccessfulWrites;
        fixture.Update();

        fixture.Service.ErrorMessage.Should().BeNull();
        fixture.Api.Ramp.Blue[200].Should().Be(fixture.Api.Ramp.Red[200]);
        fixture.Api.SuccessfulWrites.Should().Be(writes);
    }

    [Fact]
    public void I_do_not_stop_healthy_monitors_while_another_monitor_is_retrying()
    {
        using var fixture = new Fixture(includeHealthyMonitor: true);
        fixture.Update();
        fixture.Api.FailWrites = true;
        fixture.Update(WarmerTint);
        fixture.Update(new(3300, 0.7));

        fixture.Service.ErrorMessage.Should().Contain(fixture.Display.FriendlyName);
        fixture.HealthyApi.SuccessfulWrites.Should().Be(3);
        fixture.Api.WriteAttempts.Should().Be(2);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "LightBulb_Gamma_Tests_" + Guid.NewGuid().ToString("N")
        );
        private int _missingContexts;
        public DisplayDevice Display { get; } =
            HdrProfileTestData.Display with
            {
                IsHdrEnabled = false,
                SupportsMhc2 = false,
                HdrMetadata = null,
                FriendlyName = "Test SDR display",
            };
        public FakeGammaApi Api { get; } = new();
        public FakeGammaApi HealthyApi { get; } = new();
        public FakeClock Clock { get; } = new();
        public SettingsService Settings { get; } = new() { IsGammaPollingEnabled = false };
        public GammaService Service { get; }
        public int ContextCreations { get; private set; }
        public nint LastHandle { get; private set; }

        public Fixture(int missingContexts = 0, bool includeHealthyMonitor = false)
        {
            _missingContexts = missingContexts;
            Directory.CreateDirectory(_directory);
            IReadOnlyList<DisplayDevice> displays = includeHealthyMonitor
                ?
                [
                    Display,
                    Display with
                    {
                        Id = "healthy-monitor-id",
                        DeviceName = @"\\.\DISPLAY2",
                        FriendlyName = "Healthy SDR display",
                    },
                ]
                : [Display];
            foreach (var display in displays)
                Settings.SetMonitorConfiguration(display.Id, new() { IsEnabled = false });
            Service = new(Settings, () => displays, CreateContext, Clock, _directory);
            var watch = Stopwatch.StartNew();
            while (Service.Displays.Count == 0 && watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                Update();
                Thread.Yield();
            }
            Service.Displays.Count.Should().Be(displays.Count);
            foreach (var display in displays)
                Settings.SetMonitorConfiguration(display.Id, new());
        }

        private DeviceContext? CreateContext(string name)
        {
            if (name != Display.DeviceName)
                return new DeviceContext(100, HealthyApi);
            ContextCreations++;
            if (_missingContexts-- > 0)
                return null;
            return new DeviceContext(LastHandle = ContextCreations, Api);
        }

        public void Update(ColorConfiguration? configuration = null) =>
            Service.SetGamma(
                configuration ?? Tint,
                new(new(7, 0), new(18, 0)),
                Clock.GetUtcNow(),
                true,
                false,
                0,
                0,
                TimeSpan.FromMilliseconds(50)
            );

        public void Dispose()
        {
            Api.FailWrites = false;
            Api.FailReads = false;
            Api.InvalidHandles.Clear();
            Service.Dispose();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan interval) => _now += interval;
    }

    private sealed class FakeGammaApi : IDeviceGammaApi
    {
        public GammaRamp Ramp { get; private set; }
        public bool FailReads { get; set; }
        public bool FailWrites { get; set; }
        public int WriteAttempts { get; private set; }
        public int SuccessfulWrites { get; private set; }
        public HashSet<nint> InvalidHandles { get; } = [];
        public List<nint> DeletedHandles { get; } = [];

        public GammaRamp GetGammaRamp(nint handle) =>
            FailReads ? throw new Win32Exception(31, "Injected gamma read failure") : Ramp;

        public void SetGammaRamp(nint handle, GammaRamp ramp)
        {
            WriteAttempts++;
            if (FailWrites || InvalidHandles.Contains(handle))
                throw new Win32Exception(31, "Injected gamma write failure");
            SuccessfulWrites++;
            Ramp = ramp;
        }

        public void DeleteDC(nint handle) => DeletedHandles.Add(handle);
    }
}
