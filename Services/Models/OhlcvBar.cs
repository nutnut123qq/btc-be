namespace Backend.Services.Models;

/// <summary>
/// Provider-agnostic OHLCV bar contract shared by the technical indexers.
/// Deliberately exposes ONLY open/high/low/close/volume + timestamps: Binance-only
/// fields (QuoteVolume, TradeCount, TakerBuy*) exist on <see cref="KlineDto"/> but
/// not on equity candles, so indexers that consume this contract can never mistake
/// absent equity fields for real zeroes (MBB-03 contract).
/// </summary>
public interface IOhlcvBar
{
    long OpenTimeMs { get; }
    long CloseTimeMs { get; }
    decimal Open { get; }
    decimal High { get; }
    decimal Low { get; }
    decimal Close { get; }
    decimal Volume { get; }
}

/// <summary>
/// Minimal internal OHLCV bar for instruments stored in <c>InstrumentCandles</c>
/// (e.g. MBB/HOSE). Carries no provider-specific fields by construction.
/// </summary>
public sealed record OhlcvBar(
    long OpenTimeMs,
    long CloseTimeMs,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume) : IOhlcvBar;

/// <summary>
/// Decides whether two consecutive stored bars are truly adjacent for the
/// instrument's calendar. Crypto uses a fixed interval check
/// (next - prev == intervalMs); HOSE uses the next expected session open from
/// <c>VietnamMarketCalendar</c> so lunch/overnight/weekend/holiday breaks count as
/// contiguous while a missing expected bar still breaks indicator state.
/// </summary>
public delegate bool BarAdjacency(long previousOpenTimeMs, long nextOpenTimeMs);
