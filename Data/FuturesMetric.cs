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
