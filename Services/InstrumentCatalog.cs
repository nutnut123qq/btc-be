using Backend.Services.Models;

namespace Backend.Services;

/// <summary>
/// Central registry of instruments the platform knows about. Catalog membership only
/// means an instrument has a declared contract (metadata, timeframes, capabilities);
/// <see cref="ProductionSymbolPolicy"/> decides which instruments are enabled for
/// production traffic. Unknown symbols fail closed.
/// </summary>
public sealed class InstrumentCatalog
{
    public const string ContractVersion = "instrument-catalog-v2";

    /// <summary>Shared instance for non-DI construction paths (tests, service defaults).</summary>
    public static readonly InstrumentCatalog Default = new();

    private static readonly IReadOnlyList<InstrumentDescriptor> Registry = Array.AsReadOnly(
    new InstrumentDescriptor[]
    {
        new(
            Symbol: "BTCUSDT",
            Label: "BTC/USDT",
            Venue: "Binance Spot",
            AssetClass: InstrumentAssetClasses.Crypto,
            Currency: "USDT",
            Timezone: "UTC",
            DataProvider: "Binance",
            CrossCheckDataProvider: null,
            PriceBasis: "raw",
            Aliases: Array.AsReadOnly(new[] { "BTC" }),
            ActiveTimeframes: Array.AsReadOnly(new[] { "1h", "4h", "1d" }),
            DefaultTimeframe: "4h",
            Enabled: true,
            Capabilities: new(
                TechnicalAnalysis: true,
                News: true,
                Sentiment: true,
                Futures: true,
                Liquidation: true,
                OrderBook: true,
                Execution: true,
                PaperTrading: true)),
        Equity("MBB", "MBB (MB Bank)", ["1h", "1d"], decision: true),
        Equity("BID", "BID (BIDV)", ["1d"], decision: true),
        Equity("VCB", "VCB (Vietcombank)", ["1d"], decision: false),
        Equity("CTG", "CTG (VietinBank)", ["1d"], decision: false),
        new(
            Symbol: "VNINDEX",
            Label: "VN-Index",
            Venue: "HOSE",
            AssetClass: InstrumentAssetClasses.Index,
            // Quote unit, not a cash currency: the index is quoted in points.
            Currency: "index-points",
            Timezone: "Asia/Ho_Chi_Minh",
            DataProvider: "VCI",
            CrossCheckDataProvider: null,
            PriceBasis: "raw",
            Aliases: Array.AsReadOnly(Array.Empty<string>()),
            ActiveTimeframes: Array.AsReadOnly(new[] { "1d" }),
            DefaultTimeframe: "1d",
            Enabled: false,
            Capabilities: new(
                TechnicalAnalysis: false, News: false, Sentiment: false, Futures: false,
                Liquidation: false, OrderBook: false, Execution: false, PaperTrading: false,
                Ingestion: true, Decision: false)),
    });

    /// <summary>
    /// HOSE equity on the VCI adjusted series. Enabled stays false: equities never pass
    /// the BTC/crypto production-traffic gate; the equity lane checks capabilities instead.
    /// Decision instruments (MBB, BID) also carry TechnicalAnalysis; context-only peers do not.
    /// </summary>
    private static InstrumentDescriptor Equity(string symbol, string label, string[] timeframes, bool decision) => new(
        Symbol: symbol,
        Label: label,
        Venue: "HOSE",
        AssetClass: InstrumentAssetClasses.Equity,
        Currency: "VND",
        Timezone: "Asia/Ho_Chi_Minh",
        DataProvider: "VCI",
        CrossCheckDataProvider: "KBS",
        PriceBasis: "adjusted",
        Aliases: Array.AsReadOnly(Array.Empty<string>()),
        ActiveTimeframes: Array.AsReadOnly(timeframes),
        DefaultTimeframe: "1d",
        Enabled: false,
        Capabilities: new(
            TechnicalAnalysis: decision, News: false, Sentiment: false, Futures: false,
            Liquidation: false, OrderBook: false, Execution: false, PaperTrading: false,
            Ingestion: true, Decision: decision));

    private static readonly IReadOnlySet<string> KnownSymbols =
        new HashSet<string>(Registry.Select(x => x.Symbol), StringComparer.Ordinal);

    public IReadOnlyList<InstrumentDescriptor> Instruments => Registry;

    /// <summary>Trim + uppercase, then resolve declared aliases (BTC -> BTCUSDT).</summary>
    public static string Canonicalize(string? symbol)
    {
        var normalized = symbol?.Trim().ToUpperInvariant() ?? string.Empty;
        foreach (var instrument in Registry)
        {
            if (instrument.Aliases.Contains(normalized, StringComparer.Ordinal))
                return instrument.Symbol;
        }
        return normalized;
    }

    /// <summary>Returns the descriptor for a known symbol or alias; null when unknown.</summary>
    public InstrumentDescriptor? Get(string? symbol)
    {
        var canonical = Canonicalize(symbol);
        return KnownSymbols.Contains(canonical)
            ? Registry.First(x => string.Equals(x.Symbol, canonical, StringComparison.Ordinal))
            : null;
    }

    public bool IsKnown(string? symbol) => Get(symbol) is not null;
}
