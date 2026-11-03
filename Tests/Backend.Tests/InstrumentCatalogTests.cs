using Backend.Services;
using Backend.Services.Models;

namespace Backend.Tests;

public class InstrumentCatalogTests
{
    private readonly InstrumentCatalog _catalog = InstrumentCatalog.Default;

    /// <summary>VN30 basket in effect from 2026-08-03 minus MBB, BID, VCB, CTG (VN30-DATA), catalog order.</summary>
    internal static readonly string[] Vn30DataOnly =
    [
        "ACB", "BSR", "FPT", "GAS", "GVR", "HDB", "HPG", "LPB", "MCH", "MSN", "MWG", "SAB", "SHB",
        "SSB", "SSI", "STB", "TCB", "TCX", "VHM", "VIB", "VIC", "VJC", "VNM", "VPB", "VPL", "VRE",
    ];

    [Fact]
    public void Vn30Constituents_AreTechnicalOnly_AndOutsideTheReferencePeers()
    {
        var equities = _catalog.Instruments.Where(i => i.AssetClass == InstrumentAssetClasses.Equity).Select(i => i.Symbol).ToHashSet();
        Assert.Equal(30, equities.Count); // the whole basket: 26 VN30-DATA + MBB, BID, VCB, CTG
        // VN30-TA: every equity has TechnicalAnalysis; only MBB/BID have Decision (and 1h).
        foreach (var symbol in Vn30DataOnly.Append("VCB").Append("CTG"))
        {
            var i = _catalog.Get(symbol)!;
            Assert.Equal(["1d"], i.ActiveTimeframes);
            Assert.True(EquityScope.Has(i, EquityCapability.Ingestion));
            Assert.True(EquityScope.Has(i, EquityCapability.TechnicalAnalysis));
            Assert.False(EquityScope.Has(i, EquityCapability.Decision));
        }
        Assert.Equal(["BID", "MBB"], _catalog.Instruments.Where(i => EquityScope.Has(i, EquityCapability.Decision))
            .Select(i => i.Symbol).Order().ToArray());
        Assert.False(EquityScope.Has(_catalog.Get("VNINDEX")!, EquityCapability.TechnicalAnalysis));
        // Relative strength and data-quality peers stay exactly what MBB/BID used before VN30-DATA.
        Assert.Equal(["MBB", "BID", "VCB", "CTG", "VNINDEX"],
            _catalog.Instruments.Where(EquityScope.IsReferencePeer).Select(i => i.Symbol).ToArray());
    }

    [Fact]
    public void Registry_DeclaresBtcAndMbbWithContractSemantics()
    {
        var btc = _catalog.Get("BTCUSDT");
        var mbb = _catalog.Get("MBB");

        Assert.NotNull(btc);
        Assert.Equal(InstrumentAssetClasses.Crypto, btc.AssetClass);
        Assert.Equal("Binance", btc.DataProvider);
        Assert.Equal("raw", btc.PriceBasis);
        Assert.True(btc.Enabled);
        Assert.Equal(["1h", "4h", "1d"], btc.ActiveTimeframes);
        Assert.Equal("4h", btc.DefaultTimeframe);
        Assert.True(btc.Capabilities.TechnicalAnalysis);
        Assert.True(btc.Capabilities.News);
        Assert.True(btc.Capabilities.Futures);
        Assert.True(btc.Capabilities.Execution);
        Assert.True(btc.Capabilities.PaperTrading);

        Assert.NotNull(mbb);
        Assert.Equal(InstrumentAssetClasses.Equity, mbb.AssetClass);
        Assert.Equal("HOSE", mbb.Venue);
        Assert.Equal("VND", mbb.Currency);
        Assert.Equal("Asia/Ho_Chi_Minh", mbb.Timezone);
        Assert.Equal("VCI", mbb.DataProvider);
        Assert.Equal("KBS", mbb.CrossCheckDataProvider);
        Assert.Equal("adjusted", mbb.PriceBasis);
        Assert.False(mbb.Enabled); // contract registered; pipelines land in later lanes
        Assert.Equal(["1h", "1d"], mbb.ActiveTimeframes); // no synthetic 4h
        Assert.Equal("1d", mbb.DefaultTimeframe);
        Assert.True(mbb.Capabilities.TechnicalAnalysis);
        Assert.False(mbb.Capabilities.News);
        Assert.False(mbb.Capabilities.Sentiment);
        Assert.False(mbb.Capabilities.Futures);
        Assert.False(mbb.Capabilities.Liquidation);
        Assert.False(mbb.Capabilities.OrderBook);
        Assert.False(mbb.Capabilities.Execution);
        Assert.False(mbb.Capabilities.PaperTrading);
    }

    [Fact]
    public void Registry_IsStructurallySound()
    {
        var instruments = _catalog.Instruments;

        Assert.Equal(instruments.Count, instruments.Select(x => x.Symbol).Distinct(StringComparer.Ordinal).Count());
        foreach (var instrument in instruments)
        {
            Assert.Contains(instrument.DefaultTimeframe, instrument.ActiveTimeframes);
            Assert.DoesNotContain(instrument.Symbol, instrument.Aliases, StringComparer.Ordinal);
            Assert.All(instrument.ActiveTimeframes, tf =>
                Assert.True(Timeframes.IntervalToMs(tf) > 0, $"{instrument.Symbol} timeframe {tf} must be a known interval"));
        }
        // Catalog BTC scope must stay identical to the timeframe policy's BTC contract.
        Assert.Equal(ProductionTimeframePolicy.Defaults, _catalog.Get("BTCUSDT")!.ActiveTimeframes);
    }

    [Theory]
    [InlineData("BTC", "BTCUSDT")]
    [InlineData(" btcusdt ", "BTCUSDT")]
    [InlineData("mbb", "MBB")]
    [InlineData("ETHUSDT", "ETHUSDT")] // unknown symbols pass through unchanged
    [InlineData("", "")]
    public void Canonicalize_MapsAliasesAndNormalizes(string input, string expected) =>
        Assert.Equal(expected, InstrumentCatalog.Canonicalize(input));

    [Fact]
    public void Get_FailsClosedOnUnknownAndNull() =>
        Assert.Null(_catalog.Get(null));
}
