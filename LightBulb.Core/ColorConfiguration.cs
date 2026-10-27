using System;

namespace LightBulb.Core;

public readonly partial record struct ColorConfiguration(double Temperature, double Brightness)
{
    public (double Red, double Green, double Blue) GetGammaMultipliers()
    {
        if (!double.IsFinite(Temperature) || Temperature <= 0)
            throw new ArgumentOutOfRangeException(nameof(Temperature));

        if (!double.IsFinite(Brightness) || Brightness is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(Brightness));

        // Tanner Helland's temperature-to-RGB approximation, also used by the legacy backend.
        var temperature = Temperature / 100;
        var red =
            Temperature > 6600
                ? Math.Clamp(Math.Pow(temperature - 60, -0.1332047592) * 329.698727446 / 255, 0, 1)
                : 1;
        var green = Math.Clamp(
            Temperature > 6600
                ? Math.Pow(temperature - 60, -0.0755148492) * 288.1221695283 / 255
                : (Math.Log(temperature) * 99.4708025861 - 161.1195681661) / 255,
            0,
            1
        );
        var blue =
            Temperature >= 6600 ? 1
            : Temperature <= 1900 ? 0
            : Math.Clamp(
                (Math.Log(temperature - 10) * 138.5177312231 - 305.0447927307) / 255,
                0,
                1
            );

        return (red * Brightness, green * Brightness, blue * Brightness);
    }

    public ColorConfiguration WithOffset(double temperatureOffset, double brightnessOffset) =>
        new(Temperature + temperatureOffset, Brightness + brightnessOffset);

    public ColorConfiguration Clamp(
        double minimumTemperature,
        double maximumTemperature,
        double minimumBrightness,
        double maximumBrightness
    ) =>
        new(
            Math.Clamp(Temperature, minimumTemperature, maximumTemperature),
            Math.Clamp(Brightness, minimumBrightness, maximumBrightness)
        );
}

public partial record struct ColorConfiguration
{
    public static ColorConfiguration Default { get; } = new(6600, 1);
}
