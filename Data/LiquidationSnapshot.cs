using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Backend.Data;

[Table("LiquidationSnapshots")]
public class LiquidationSnapshot
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(32)]
    public string Symbol { get; set; } = "BTCUSDT";

    [MaxLength(16)]
    public string Timeframe { get; set; } = "1h";

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public double CurrentPrice { get; set; }

    public double TotalLongLiqUsdt { get; set; }

    public double TotalShortLiqUsdt { get; set; }

    public string HeatmapJson { get; set; } = "[]";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
