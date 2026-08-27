using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Backend.Data;

[Table("PaperTrades")]
public class PaperTrade
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }
    
    [MaxLength(32)]
    public string Symbol { get; set; } = null!;
    
    [MaxLength(16)]
    public string Timeframe { get; set; } = null!;
    
    public long WindowEndMs { get; set; }
    public long EntryTimeMs { get; set; }
    public long ExitTimeMs { get; set; }
    
    [MaxLength(8)]
    public string Side { get; set; } = null!;
    
    public double? Confidence { get; set; }
    public double? ProbDown { get; set; }
    public double? ProbSideways { get; set; }
    public double? ProbUp { get; set; }
    public double? EntryPrice { get; set; }
    public double? ExitPrice { get; set; }
    public double? NetReturn { get; set; }
    
    [MaxLength(8)]
    public string Status { get; set; } = "open";
    
    [MaxLength(128)]
    public string? ModelVersion { get; set; }
    
    // --- Phase 10: Enhanced Paper Trading fields ---
    public double? PositionSizeUsdt { get; set; }
    public double? TakeProfitPrice { get; set; }
    public double? StopLossPrice { get; set; }
    public double? Atr14 { get; set; }
    
    [MaxLength(16)]
    public string? ExitReason { get; set; }  // TP, SL, TIMEOUT, SIGNAL
    
    public double? BalanceAfter { get; set; }
    
    [MaxLength(16)]
    public string? EnsembleDirection { get; set; }  // Bullish, Bearish, Sideways
    
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }

    // --- User Data Stream / Live Execution fields ---
    public long? OrderId { get; set; }

    [MaxLength(64)]
    public string? ClientOrderId { get; set; }

    public double? ExecutedQty { get; set; }
    public double? Commission { get; set; }

    [MaxLength(16)]
    public string? CommissionAsset { get; set; }

    public double? RealizedPnL { get; set; }
}

