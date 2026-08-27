using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Backend.Data;

[Table("WalletBalanceSnapshots")]
public class WalletBalanceSnapshot
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(32)]
    public string Asset { get; set; } = "USDT";

    public decimal WalletBalance { get; set; }
    public decimal CrossWalletBalance { get; set; }
    public decimal BalanceChange { get; set; }
    public decimal TotalUnrealizedProfit { get; set; }

    [MaxLength(32)]
    public string? EventReasonType { get; set; }

    [MaxLength(32)]
    public string? Symbol { get; set; }

    public decimal? PositionAmount { get; set; }
    public decimal? EntryPrice { get; set; }
    public decimal? UnrealizedPnL { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
