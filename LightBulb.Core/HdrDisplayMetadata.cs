namespace LightBulb.Core;

public sealed record HdrDisplayMetadata(
    double RedX,
    double RedY,
    double GreenX,
    double GreenY,
    double BlueX,
    double BlueY,
    double WhiteX,
    double WhiteY,
    double MinimumLuminance,
    double PeakLuminance,
    double FullFrameLuminance
);
