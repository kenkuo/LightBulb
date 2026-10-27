using System;

namespace LightBulb.Core;

public sealed class ColorConfigurationSmoother
{
    private ColorConfiguration? _source;
    private ColorConfiguration? _target;

    public ColorConfiguration Current { get; private set; } = ColorConfiguration.Default;

    public ColorConfiguration Update(
        ColorConfiguration target,
        bool isSmooth,
        TimeSpan maxDuration,
        TimeSpan interval
    )
    {
        if (!isSmooth || maxDuration.TotalSeconds < 0.1 || Current == target)
        {
            Current = target;
            _source = null;
            _target = null;
            return Current;
        }

        if (_target != target || _source is null)
        {
            _source = Current;
            _target = target;
        }

        var temperatureStep = Math.Max(
            Math.Abs(target.Temperature - _source.Value.Temperature)
                / maxDuration.TotalSeconds
                * interval.TotalSeconds,
            30
        );
        var brightnessStep = Math.Max(
            Math.Abs(target.Brightness - _source.Value.Brightness)
                / maxDuration.TotalSeconds
                * interval.TotalSeconds,
            0.08
        );
        var steps = Math.Max(
            Math.Abs(target.Temperature - Current.Temperature) / temperatureStep,
            Math.Abs(target.Brightness - Current.Brightness) / brightnessStep
        );

        Current =
            steps <= 1
                ? target
                : new(
                    Current.Temperature + (target.Temperature - Current.Temperature) / steps,
                    Current.Brightness + (target.Brightness - Current.Brightness) / steps
                );

        return Current;
    }
}
