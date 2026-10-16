using System.ComponentModel.DataAnnotations;

namespace Backend.Services.Models;

public static class InstrumentAssetClasses
{
    public const string Crypto = "crypto";
    public const string Equity = "equity";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Crypto,
        Equity
    };
}

/// <summary>
/// Feature-family flags for an instrument. <see cref="TechnicalAnalysis"/> is the only
/// family shared by every instrument; the remaining flags are crypto-only features
/// (news/sentiment/futures/liquidation/orderbook/execution/paper trading) that equity
/// instruments such as MBB must never report as supported.
/// </summary>
public sealed record InstrumentCapabilities(
    bool TechnicalAnalysis,
    bool News,
    bool Sentiment,
    bool Futures,
    bool Liquidation,
    bool OrderBook,
    bool Execution,
    bool PaperTrading);

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
