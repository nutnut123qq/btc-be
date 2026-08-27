namespace Backend.Services.Models;

public class TradeExecutionAlertDto
{
    public string Symbol { get; set; } = "BTCUSDT";
    public string Side { get; set; } = "LONG";
    public string Status { get; set; } = "TAKE PROFIT FILLED";
    public double EntryPrice { get; set; }
    public double? ExitPrice { get; set; }
    public double ExecutedQty { get; set; }
    public double? RealizedPnL { get; set; }
    public double? RoiPercent { get; set; }
    public string? DurationText { get; set; }
    public bool IsExit { get; set; } = true;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
