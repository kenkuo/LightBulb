using System;

namespace LightBulb.Core;

public sealed record MonitorConfiguration
{
    public bool IsEnabled { get; init; } = true;

    public bool UseCustomConfiguration { get; init; }

    public bool UseHdrProfile { get; init; }

    public bool UseHdrTransitions { get; init; }

    public ColorConfiguration DayConfiguration { get; init; } = ColorConfiguration.Default;

    public ColorConfiguration NightConfiguration { get; init; } = new(3900, 0.85);

    public ColorConfiguration GetTargetConfiguration(
        ColorConfiguration globalConfiguration,
        SolarTimes solarTimes,
        TimeSpan transitionDuration,
        double transitionOffset,
        DateTimeOffset instant,
        bool isActive,
        bool defaultToDay,
        double temperatureOffset,
        double brightnessOffset,
        bool useDiscreteTransitions = false
    )
    {
        if (!IsEnabled)
            return ColorConfiguration.Default;

        if (!UseCustomConfiguration && !useDiscreteTransitions)
            return globalConfiguration;

        if (!isActive)
            return defaultToDay
                ? DayConfiguration.Clamp(500, 20_000, 0.1, 1)
                : ColorConfiguration.Default;

        var target = useDiscreteTransitions
            ? Cycle.SelectConfiguration(
                solarTimes,
                DayConfiguration,
                NightConfiguration,
                transitionDuration,
                transitionOffset,
                instant
            )
            : Cycle.InterpolateConfiguration(
                solarTimes,
                DayConfiguration,
                NightConfiguration,
                transitionDuration,
                transitionOffset,
                instant
            );

        return target.WithOffset(temperatureOffset, brightnessOffset).Clamp(500, 20_000, 0.1, 1);
    }
}
