using System.ComponentModel.DataAnnotations;

namespace Backend.Services.Models;

public static class InstrumentAssetClasses
{
    public const string Crypto = "crypto";
    public const string Equity = "equity";

    /// <summary>Market index (VNINDEX): quoted in index points, never traded, no lot/fee/tax.</summary>
    public const string Index = "index";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Crypto,
        Equity,
        Index
    };
}

/// <summary>
/// Feature-family flags for an instrument. <see cref="TechnicalAnalysis"/> is the only
/// family shared by every instrument; the remaining flags are crypto-only features
/// (news/sentiment/futures/liquidation/orderbook/execution/paper trading) that equity
/// instruments such as MBB must never report as supported.
/// Equity-lane flags (instrument-catalog-v2, additive):
/// <see cref="Ingestion"/> = the InstrumentCandles ingestion lane exists for the symbol;
/// <see cref="Decision"/> = position/decision/observation/base-rates/context/dossier lane.
/// Context-only instruments (VCB, CTG, VNINDEX) are Ingestion without Decision.
/// </summary>
public sealed record InstrumentCapabilities(
    bool TechnicalAnalysis,
    bool News,
    bool Sentiment,
    bool Futures,
    bool Liquidation,
    bool OrderBook,
    bool Execution,
    bool PaperTrading,
    bool Ingestion = false,
    bool Decision = false);

/// <summary>
/// Shared instrument contract. <paramref name="Enabled"/> marks whether the instrument is
/// enabled for production request traffic today; a registered instrument with
/// <c>Enabled = false</c> has a declared contract but no live pipelines yet.
/// </summary>
public sealed record InstrumentDescriptor(
    [property: Required] string Symbol,
    [property: Required] string Label,
    [property: Required] string Venue,
    [property: Required] string AssetClass,
    [property: Required] string Currency,
    [property: Required] string Timezone,
    [property: Required] string DataProvider,
    string? CrossCheckDataProvider,
    [property: Required] string PriceBasis,
    [property: Required] IReadOnlyList<string> Aliases,
    [property: Required] IReadOnlyList<string> ActiveTimeframes,
    [property: Required] string DefaultTimeframe,
    bool Enabled,
    [property: Required] InstrumentCapabilities Capabilities);

public sealed record InstrumentsResponse(
    [property: Required] string ContractVersion,
    [property: Required] DateTime GeneratedAtUtc,
    [property: Required] IReadOnlyList<InstrumentDescriptor> Items);
