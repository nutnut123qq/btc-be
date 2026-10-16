using System.ComponentModel.DataAnnotations.Schema;

namespace Backend.Data;

/// <summary>
/// Provider-agnostic OHLCV candle store for non-Binance instruments (MBB/HOSE via VCI).
/// The Binance-only fields on <see cref="Kline"/> (QuoteVolume, TradeCount, TakerBuy*)
/// intentionally do not exist here: equity providers do not publish them, and writing
/// MBB into Klines with zero-filled fields would let downstream readers mistake absent
/// data for real zeroes. Provenance columns (Provider, PriceBasis, Venue, SourceTimezone)
/// make the adjusted-price basis explicit so VCI and KBS histories are never silently
/// mixed. <see cref="Revision"/> increments when a provider history rewrite changes a
/// stored bar after first insert, giving downstream recompute lanes a detectable signal.
/// </summary>
public class InstrumentCandle
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "";
    public string Timeframe { get; set; } = "";

    /// <summary>Bar open time, Unix ms UTC. For MBB the local (Asia/Ho_Chi_Minh, UTC+7) open.</summary>
    public long OpenTimeMs { get; set; }

    /// <summary>
    /// Session-aware modeled close, Unix ms UTC. HOSE 1h bars close at session boundaries
    /// (the 11:00 bar at 11:30, the 14:00 bar at 14:45 local), not open+60m.
    /// </summary>
    public long CloseTimeMs { get; set; }

    [Column(TypeName = "numeric")]
    public decimal Open { get; set; }
    [Column(TypeName = "numeric")]
    public decimal High { get; set; }
    [Column(TypeName = "numeric")]
    public decimal Low { get; set; }
    [Column(TypeName = "numeric")]
    public decimal Close { get; set; }
    [Column(TypeName = "numeric")]
    public decimal Volume { get; set; }

    /// <summary>Primary write provider, e.g. "VCI". Cross-check providers never write here.</summary>
    public string Provider { get; set; } = "";

    /// <summary>Price basis of the stored series, e.g. "adjusted". Never mixed per symbol.</summary>
    public string PriceBasis { get; set; } = "";

    public string Venue { get; set; } = "";
    public string SourceTimezone { get; set; } = "";

    /// <summary>When this row was first fetched from the provider.</summary>
    public DateTime FetchedAtUtc { get; set; }

    /// <summary>When this row was last written; later than FetchedAtUtc on provider revisions.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>0 = first write; &gt;0 = provider rewrote the bar (e.g. adjustment change).</summary>
    public int Revision { get; set; }
}
