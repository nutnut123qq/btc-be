using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Backend.Data;

[Table("FuturesMetrics")]
public class FuturesMetric
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(32)]
    public string Symbol { get; set; } = null!;

    public long OpenTimeMs { get; set; }

    /// <summary>Timestamp emitted by the upstream source for this observation.</summary>
    public long? SourceEventTimeMs { get; set; }

    /// <summary>When this process actually received the observation. Null for legacy rows.</summary>
    public DateTimeOffset? ReceivedAtUtc { get; set; }

    /// <summary>Earliest decision timestamp at which this row may be consumed.</summary>
    public long? AvailableTimeMs { get; set; }

    [MaxLength(128)]
    public string? Source { get; set; }

    [MaxLength(32)]
    public string? MarketType { get; set; }

    /// <summary>True for legacy/backfilled rows without an original live receipt.</summary>
    public bool IsReconstructed { get; set; } = true;

    public double? OpenInterest { get; set; }
    public double? OpenInterestValue { get; set; }
    public double? TopTraderLsCountRatio { get; set; }
    public double? TopTraderLsSumRatio { get; set; }
    public double? GlobalLsRatio { get; set; }
    public double? TakerBuySellVolRatio { get; set; }
    public double? FundingRate { get; set; }
    public double? MarkPrice { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
