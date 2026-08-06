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
    
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }
}
