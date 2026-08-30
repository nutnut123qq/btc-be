using Backend.Data;

namespace Backend.Services;

public static class PaperTradeMetrics
{
    public static double? NetReturn(PaperTrade trade)
    {
        if (!string.Equals(trade.Status, "closed", StringComparison.OrdinalIgnoreCase))
            return trade.NetReturn;

        if (trade.PositionSizeUsdt is > 0 && trade.RealizedPnL.HasValue)
        {
            return (trade.RealizedPnL.Value - (trade.Commission ?? 0)) / trade.PositionSizeUsdt.Value;
        }

        // Older Ensemble-5Layer rows stored percentage points (0.5 meant 0.5%).
        // New rows also carry RealizedPnL, so this branch only normalizes legacy records.
        if (trade.RealizedPnL is null
            && trade.NetReturn.HasValue
            && trade.ModelVersion?.StartsWith("Ensemble-5Layer", StringComparison.OrdinalIgnoreCase) == true)
        {
            return trade.NetReturn.Value / 100.0;
        }

        // Canonical NetReturn is a fraction. Other values outside [-1, 1] are ambiguous
        // legacy data, so recompute from prices when possible rather than guessing.
        if (trade.NetReturn is >= -1 and <= 1)
            return trade.NetReturn;

        if (trade.EntryPrice is > 0 && trade.ExitPrice.HasValue)
        {
            var gross = string.Equals(trade.Side, "short", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trade.Side, "sell", StringComparison.OrdinalIgnoreCase)
                ? (trade.EntryPrice.Value - trade.ExitPrice.Value) / trade.EntryPrice.Value
                : (trade.ExitPrice.Value - trade.EntryPrice.Value) / trade.EntryPrice.Value;
            var fee = trade.PositionSizeUsdt is > 0 ? (trade.Commission ?? 0) / trade.PositionSizeUsdt.Value : 0;
            return gross - fee;
        }

        return null;
    }

    public static double? RealizedPnlUsdt(PaperTrade trade)
    {
        if (trade.RealizedPnL.HasValue)
            return trade.RealizedPnL.Value - (trade.Commission ?? 0);

        var netReturn = NetReturn(trade);
        return netReturn.HasValue ? (trade.PositionSizeUsdt ?? 2000.0) * netReturn.Value : null;
    }
}
