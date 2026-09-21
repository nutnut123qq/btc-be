namespace Backend.Services;

/// <summary>Single source of truth for the production market scope.</summary>
public sealed class ProductionSymbolPolicy
{
    public const string Symbol = "BTCUSDT";

    public static string Canonicalize(string? symbol)
    {
        var canonical = symbol?.Trim().ToUpperInvariant() ?? string.Empty;
        return canonical == "BTC" ? Symbol : canonical;
    }

    public bool IsActive(string? symbol) =>
        string.Equals(Canonicalize(symbol), Symbol, StringComparison.Ordinal);

    public string InactiveMessage(string? symbol) =>
        $"symbol '{Canonicalize(symbol)}' is inactive. The production scope is {Symbol} only.";

    public string EnsureActive(string? symbol)
    {
        var canonical = Canonicalize(symbol);
        if (!IsActive(canonical))
            throw new ArgumentException(InactiveMessage(symbol), nameof(symbol));
        return canonical;
    }

    public string NormalizeListAndEnsureActive(string symbols)
    {
        var normalized = symbols
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(EnsureActive)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
            throw new ArgumentException("At least one symbol is required.", nameof(symbols));
        return string.Join(',', normalized);
    }
}
