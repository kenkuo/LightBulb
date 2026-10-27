using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace LightBulb.Core.Tests;

public class MonitorConfigurationSpecs
{
    private static readonly SolarTimes SolarTimes = new(new TimeOnly(7, 0), new TimeOnly(18, 0));

    private static ColorConfiguration Resolve(
        MonitorConfiguration monitor,
        bool active = true,
        bool defaultToDay = false,
        int hour = 22,
        double temperatureOffset = 0,
        double brightnessOffset = 0,
        int minute = 0,
        bool useDiscreteTransitions = false
    ) =>
        monitor.GetTargetConfiguration(
            new(6600, 1),
            SolarTimes,
            TimeSpan.FromMinutes(40),
            0,
            new DateTimeOffset(2026, 1, 1, hour, minute, 0, TimeSpan.Zero),
            active,
            defaultToDay,
            temperatureOffset,
            brightnessOffset,
            useDiscreteTransitions
        );

    [Fact]
    public void I_inherit_the_global_configuration_unless_overrides_are_enabled() =>
        Resolve(new MonitorConfiguration { NightConfiguration = new(3000, 0.5) })
            .Should()
            .Be(ColorConfiguration.Default);

    [Fact]
    public void I_have_independent_day_and_night_values_even_when_global_values_are_identical()
    {
        var monitor = new MonitorConfiguration
        {
            UseCustomConfiguration = true,
            DayConfiguration = new(6200, 0.95),
            NightConfiguration = new(3100, 0.6),
        };
        Resolve(monitor, hour: 12).Should().Be(monitor.DayConfiguration);
        Resolve(monitor).Should().Be(monitor.NightConfiguration);
        Resolve(monitor, temperatureOffset: 100, brightnessOffset: 0.05)
            .Should()
            .Be(new ColorConfiguration(3200, 0.65));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void I_restore_neutral_or_monitor_specific_day_values_when_paused(
        bool useDiscreteTransitions
    )
    {
        var monitor = new MonitorConfiguration
        {
            UseCustomConfiguration = true,
            DayConfiguration = new(6000, 0.9),
            NightConfiguration = new(3200, 0.7),
        };
        Resolve(monitor, active: false, useDiscreteTransitions: useDiscreteTransitions)
            .Should()
            .Be(ColorConfiguration.Default);
        Resolve(
                monitor,
                active: false,
                defaultToDay: true,
                temperatureOffset: -1000,
                useDiscreteTransitions: useDiscreteTransitions
            )
            .Should()
            .Be(monitor.DayConfiguration);
        Resolve(
                monitor with
                {
                    IsEnabled = false,
                },
                defaultToDay: true,
                useDiscreteTransitions: useDiscreteTransitions
            )
            .Should()
            .Be(ColorConfiguration.Default);
    }

    [Theory]
    [InlineData(6, 20, false)]
    [InlineData(6, 39, false)]
    [InlineData(6, 40, true)]
    [InlineData(6, 59, true)]
    [InlineData(18, 0, true)]
    [InlineData(18, 19, true)]
    [InlineData(18, 20, false)]
    [InlineData(18, 39, false)]
    public void I_hold_fixed_HDR_endpoints_until_each_transition_midpoint(
        int hour,
        int minute,
        bool isDay
    )
    {
        var monitor = new MonitorConfiguration
        {
            UseCustomConfiguration = true,
            UseHdrProfile = true,
            DayConfiguration = new(6200, 0.95),
            NightConfiguration = new(3100, 0.6),
        };

        Resolve(
                monitor,
                hour: hour,
                minute: minute,
                temperatureOffset: 100,
                brightnessOffset: 0.02,
                useDiscreteTransitions: true
            )
            .Should()
            .Be(
                (isDay ? monitor.DayConfiguration : monitor.NightConfiguration).WithOffset(
                    100,
                    0.02
                )
            );
    }

    [Theory]
    [InlineData(7, 19, true)]
    [InlineData(7, 20, false)]
    [InlineData(17, 39, false)]
    [InlineData(17, 40, true)]
    public void I_apply_transition_offsets_to_the_HDR_switch_time(
        int hour,
        int minute,
        bool isNight
    )
    {
        var day = new ColorConfiguration(6200, 0.95);
        var night = new ColorConfiguration(3100, 0.6);

        Cycle
            .SelectConfiguration(
                SolarTimes,
                day,
                night,
                TimeSpan.FromMinutes(40),
                1,
                new DateTimeOffset(2026, 1, 1, hour, minute, 0, TimeSpan.Zero)
            )
            .Should()
            .Be(isNight ? night : day);
    }

    [Theory]
    [InlineData(23, 59, true)]
    [InlineData(0, 10, false)]
    public void I_keep_the_HDR_endpoint_stable_when_a_transition_crosses_midnight(
        int hour,
        int minute,
        bool isDay
    )
    {
        var day = new ColorConfiguration(6200, 0.95);
        var night = new ColorConfiguration(3100, 0.6);

        Cycle
            .SelectConfiguration(
                new(new TimeOnly(7, 0), new TimeOnly(23, 50)),
                day,
                night,
                TimeSpan.FromMinutes(40),
                0,
                new DateTimeOffset(2026, 1, 1, hour, minute, 0, TimeSpan.Zero)
            )
            .Should()
            .Be(isDay ? day : night);
    }

    [Fact]
    public void I_clamp_offsets_to_the_supported_monitor_ranges()
    {
        var monitor = new MonitorConfiguration { UseCustomConfiguration = true };
        Resolve(monitor, temperatureOffset: -100_000, brightnessOffset: -10)
            .Should()
            .Be(new ColorConfiguration(500, 0.1));
        Resolve(monitor, temperatureOffset: 100_000, brightnessOffset: 10)
            .Should()
            .Be(new ColorConfiguration(20_000, 1));
    }

    [Fact]
    public void I_preserve_monitor_identity_and_configuration_during_serialization()
    {
        const string id = @"\\?\DISPLAY#MONITOR#unique-id";
        var monitors = new Dictionary<string, MonitorConfiguration>
        {
            [id] = new()
            {
                UseCustomConfiguration = true,
                UseHdrProfile = true,
                UseHdrTransitions = true,
                DayConfiguration = new(6000, 0.9),
                NightConfiguration = new(3200, 0.7),
            },
        };
        var restored = JsonSerializer.Deserialize<Dictionary<string, MonitorConfiguration>>(
            JsonSerializer.Serialize(monitors)
        );
        restored.Should().BeEquivalentTo(monitors);
    }

    [Fact]
    public void I_preserve_the_legacy_temperature_multipliers_and_neutral_configuration()
    {
        ColorConfiguration.Default.GetGammaMultipliers().Should().Be((1, 1, 1));
        var (red, green, blue) = new ColorConfiguration(3900, 0.85).GetGammaMultipliers();
        red.Should().Be(0.85);
        green
            .Should()
            .BeApproximately((Math.Log(39) * 99.4708025861 - 161.1195681661) / 255 * 0.85, 1e-12);
        blue.Should()
            .BeApproximately((Math.Log(29) * 138.5177312231 - 305.0447927307) / 255 * 0.85, 1e-12);
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(0, 1)]
    [InlineData(3900, double.NaN)]
    [InlineData(3900, -0.1)]
    [InlineData(3900, 1.1)]
    public void I_reject_non_finite_or_invalid_color_values(double temperature, double brightness)
    {
        Action action = () => new ColorConfiguration(temperature, brightness).GetGammaMultipliers();
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void I_smooth_custom_monitors_with_the_same_steps_as_the_global_configuration()
    {
        var smoother = new ColorConfigurationSmoother();
        var target = new ColorConfiguration(3500, 0.8);
        var first = smoother.Update(
            target,
            true,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(50)
        );
        first.Temperature.Should().BeApproximately(6569, 1e-9);
        first.Brightness.Should().BeApproximately(0.998, 1e-9);
        for (var i = 0; i < 110; i++)
            smoother.Update(target, true, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50));
        smoother.Current.Should().Be(target);
        smoother
            .Update(
                ColorConfiguration.Default,
                false,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(50)
            )
            .Should()
            .Be(ColorConfiguration.Default);
    }

    [Theory]
    [InlineData(6600, 0.8)]
    [InlineData(3500, 1)]
    [InlineData(6600, 1)]
    public void I_do_not_produce_NaN_when_only_one_smoothing_channel_changes(
        double temperature,
        double brightness
    )
    {
        var smoother = new ColorConfigurationSmoother();
        for (var i = 0; i < 200; i++)
            smoother.Update(
                new(temperature, brightness),
                true,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(50)
            );
        smoother.Current.Should().Be(new ColorConfiguration(temperature, brightness));
    }
}
