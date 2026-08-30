namespace Backend.Services.Models;

public sealed record VolumeProfileBinDto(
    double PriceLevel,
    double Volume,
    double VolumePct,
    bool IsPoc,
    bool IsValueArea);
