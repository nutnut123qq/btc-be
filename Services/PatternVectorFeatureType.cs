namespace Backend.Services;

public static class PatternVectorFeatureType
{
    public const string Open = "open";
    public const string High = "high";
    public const string Low = "low";
    public const string Close = "close";
    public const string All = "all";
    public const string ReturnsShape = "returns_shape";
    public const string ReturnsLog = "returns_log";
    public const string VolumeNorm = "volume_norm";
    public const string Volatility = "volatility";
    public const string Trend = "trend";

    public static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        Open, High, Low, Close, All, ReturnsShape, ReturnsLog, VolumeNorm, Volatility, Trend
    };

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return All;
        var value = input.Trim().ToLowerInvariant();
        return Allowed.Contains(value) ? value : string.Empty;
    }

    public static int VectorDim(string featureType, int windowSize)
    {
        if (string.Equals(featureType, All, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(featureType, ReturnsShape, StringComparison.OrdinalIgnoreCase))
            return windowSize * 4;
        return windowSize;
    }
}
