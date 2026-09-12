using Backend.Options;
using Microsoft.Extensions.Options;

namespace Backend.Services;

/// <summary>
/// Nguồn duy nhất cho các khung nến production. Dữ liệu lịch sử ở khung khác vẫn đọc được,
/// nhưng mọi pipeline ghi/backfill/rebuild phải kiểm tra policy này.
/// </summary>
public sealed class ProductionTimeframePolicy
{
    public static readonly IReadOnlyList<string> Defaults = ["1h", "4h", "1d"];

    private readonly HashSet<string> _activeSet;

    public ProductionTimeframePolicy(IOptions<ProductionTimeframeOptions>? options = null)
    {
        var configured = options?.Value.Active ?? Defaults;
        var active = configured
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Canonicalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (active.Length == 0 || active.Any(x => Timeframes.IntervalToMs(x) <= 0))
            throw new InvalidOperationException("ProductionTimeframes:Active must contain valid Binance intervals.");
        if (!new HashSet<string>(active, StringComparer.OrdinalIgnoreCase).SetEquals(Defaults))
            throw new InvalidOperationException("ProductionTimeframes:Active must contain exactly: 1h, 4h, 1d.");

        Active = active;
        _activeSet = new HashSet<string>(active, StringComparer.OrdinalIgnoreCase);

        var configuredDefault = Canonicalize(options?.Value.Default);
        Default = string.IsNullOrWhiteSpace(configuredDefault) ? "4h" : configuredDefault;
        if (!string.Equals(Default, "4h", StringComparison.Ordinal))
            throw new InvalidOperationException("ProductionTimeframes:Default must be exactly 4h.");
    }

    public IReadOnlyList<string> Active { get; }

    public string Default { get; }

    public static string Canonicalize(string? timeframe) =>
        timeframe?.Trim().ToLowerInvariant() ?? string.Empty;

    public bool IsActive(string? timeframe) => _activeSet.Contains(Canonicalize(timeframe));

    public string InactiveMessage(string timeframe) =>
        $"timeframe '{timeframe}' is inactive for production mutations. Active timeframes: {string.Join(", ", Active)}.";

    public string EnsureActive(string timeframe)
    {
        var canonical = Canonicalize(timeframe);
        if (!IsActive(canonical))
            throw new ArgumentException(InactiveMessage(timeframe), nameof(timeframe));
        return canonical;
    }

    public IReadOnlyList<string> NormalizeAndEnsureActive(IEnumerable<string> timeframes)
    {
        var normalized = timeframes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Canonicalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
            throw new ArgumentException("At least one timeframe is required.", nameof(timeframes));
        return normalized.Select(EnsureActive).ToArray();
    }
}
