using Backend.Data;
using Backend.Services;

namespace Backend.Tests;

public class PaperTradeMetricsTests
{
    [Fact]
    public void LegacyEnsemblePercentagePoints_AreNormalizedToFraction()
    {
        var trade = new PaperTrade
        {
            Status = "closed",
            ModelVersion = "Ensemble-5Layer",
            NetReturn = 0.35
        };

        var netReturn = PaperTradeMetrics.NetReturn(trade);
        Assert.True(netReturn.HasValue);
        Assert.Equal(0.0035, netReturn.Value, 8);
    }

    [Fact]
    public void NetReturn_UsesAuthoritativeRealizedPnl()
    {
        var trade = ClosedTrade();
        trade.PositionSizeUsdt = 2000;
        trade.RealizedPnL = 60;
        trade.Commission = 0.08;
        trade.NetReturn = 2.996;

        var netReturn = PaperTradeMetrics.NetReturn(trade);
        var realizedPnl = PaperTradeMetrics.RealizedPnlUsdt(trade);
        Assert.True(netReturn.HasValue);
        Assert.True(realizedPnl.HasValue);
        Assert.Equal((60 - 0.08) / 2000, netReturn.Value, 8);
        Assert.Equal(59.92, realizedPnl.Value, 8);
    }

    [Fact]
    public void NetReturn_RecomputesLegacyPercentagePointValue()
    {
        var trade = ClosedTrade();
        trade.EntryPrice = 106.14;
        trade.ExitPrice = 109.32;
        trade.NetReturn = 2.996;

        var netReturn = PaperTradeMetrics.NetReturn(trade);
        Assert.True(netReturn.HasValue);
        Assert.Equal((109.32 - 106.14) / 106.14, netReturn.Value, 8);
    }

    [Fact]
    public void NetReturn_PreservesCanonicalFraction()
    {
        var trade = ClosedTrade();
        trade.NetReturn = 0.02996;

        Assert.Equal(0.02996, PaperTradeMetrics.NetReturn(trade));
    }

    [Fact]
    public void NetReturn_DoesNotGuessAmbiguousLegacyValue()
    {
        var trade = ClosedTrade();
        trade.NetReturn = 2.5;
        trade.EntryPrice = null;
        trade.ExitPrice = null;

        Assert.Null(PaperTradeMetrics.NetReturn(trade));
    }

    private static PaperTrade ClosedTrade() => new()
    {
        Symbol = "BTCUSDT",
        Timeframe = "1h",
        Side = "long",
        Status = "closed"
    };
}
