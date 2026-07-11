using Backend.Data;
using Backend.Services;
using Backend.Services.Models;

namespace Backend.Tests;

/// <summary>
/// Direct tests of the label/feature math that determines dataset correctness
/// (previously untested — the rebuild tests used fakes with hardcoded rows).
/// </summary>
public class MlDatasetMathTests
{
    private static List<Kline> Series(params double[] closes)
    {
        var list = new List<Kline>();
        for (int i = 0; i < closes.Length; i++)
            list.Add(new Kline { Timeframe = "1h", OpenTimeMs = i * 3_600_000L, Close = (decimal)closes[i] });
        return list;
    }

    // ---- BarsForHorizon: the flooring-bug fix ----
    [Theory]
    [InlineData("1h", "1d", 24)]
    [InlineData("1m", "1d", 1440)]
    [InlineData("1h", "1h", 1)]
    [InlineData("4h", "1d", 6)]
    public void BarsForHorizon_converts_correctly(string tf, string hz, int expected)
        => Assert.Equal(expected, MlDatasetService.BarsForHorizon(tf, hz));

    [Theory]
    [InlineData("4h", "1h")]   // horizon finer than timeframe -> 0 (old code faked it to 1 bar)
    [InlineData("1d", "1h")]
    [InlineData("1d", "4h")]
    public void BarsForHorizon_is_zero_when_horizon_finer_than_timeframe(string tf, string hz)
        => Assert.Equal(0, MlDatasetService.BarsForHorizon(tf, hz));

    // ---- FutureReturn: forward-only, honest tail ----
    [Fact]
    public void FutureReturn_is_forward_pct_and_null_at_tail()
    {
        var k = Series(100, 101, 110);
        Assert.Equal(10.0, MlDatasetService.FutureReturn(k, 0, 2)!.Value, 6); // 100 -> 110
        Assert.Equal(1.0, MlDatasetService.FutureReturn(k, 0, 1)!.Value, 6);  // 100 -> 101
        Assert.Null(MlDatasetService.FutureReturn(k, 0, 3));                  // beyond series end
        Assert.Null(MlDatasetService.FutureReturn(k, 2, 1));                  // last bar has no future
    }

    // ---- Direction: per-horizon threshold ----
    [Theory]
    [InlineData(0.5, 0.3, 1)]
    [InlineData(-0.5, 0.3, -1)]
    [InlineData(0.2, 0.3, 0)]
    [InlineData(1.5, 1.2, 1)]    // +1.5% is UP at the 1d band (1.2%) ...
    [InlineData(1.5, 3.0, 0)]    // ... but SIDEWAY at the 7d band (3.0%)
    [InlineData(0.3, 0.3, 0)]    // exactly on the band is SIDEWAY (strict >, no leak into up)
    [InlineData(-0.3, 0.3, 0)]
    public void Direction_respects_threshold(double ret, double thr, int expected)
        => Assert.Equal(expected, MlDatasetService.Direction(ret, thr));

    [Fact]
    public void Direction_null_return_is_null()
        => Assert.Null(MlDatasetService.Direction(null, 0.3));

    [Fact]
    public void DirectionThreshold_grows_with_horizon()
    {
        Assert.True(MlDatasetService.DirectionThreshold("1h") < MlDatasetService.DirectionThreshold("4h"));
        Assert.True(MlDatasetService.DirectionThreshold("4h") < MlDatasetService.DirectionThreshold("1d"));
        Assert.True(MlDatasetService.DirectionThreshold("1d") < MlDatasetService.DirectionThreshold("7d"));
    }

    // ---- ObvEmaDist: clamp the divide-by-near-zero blowup ----
    [Fact]
    public void ObvEmaDist_clamps_the_near_zero_blowup()
    {
        var ind = new TechnicalIndicator { Obv = 1_000_000, ObvEma50 = 0.0001 };
        var d = MlDatasetService.ObvEmaDist(ind);
        Assert.NotNull(d);
        Assert.InRange(d!.Value, -1000.0, 1000.0); // ~1e12 without the clamp
    }

    [Fact]
    public void ObvEmaDist_normal_case_is_unclamped()
    {
        var ind = new TechnicalIndicator { Obv = 110, ObvEma50 = 100 };
        Assert.Equal(10.0, MlDatasetService.ObvEmaDist(ind)!.Value, 6);
    }

    [Fact]
    public void ObvEmaDist_null_when_missing_data()
        => Assert.Null(MlDatasetService.ObvEmaDist(new TechnicalIndicator { Obv = null, ObvEma50 = 100 }));

    // ---- EncodePattern: deterministic, distinct, stable ----
    [Fact]
    public void EncodePattern_is_deterministic_distinct_and_covers_all_patterns()
    {
        var names = Enum.GetNames<SingleCandlePattern>().Where(n => n != "None")
            .Concat(Enum.GetNames<MultiCandlePattern>().Where(n => n != "None"))
            .ToArray();

        var codes = names.Select(MlDatasetService.EncodePattern).ToArray();
        Assert.All(codes, c => Assert.True(c > 0));            // every known pattern maps non-zero
        Assert.Equal(names.Length, codes.Distinct().Count());  // no collisions
        Assert.Equal(0, MlDatasetService.EncodePattern("NotARealPattern"));

        // Pin specific codes to their position: a reorder/insert that shifts existing codes
        // (the exact regression the fixed ordering prevents) must fail here.
        Assert.Equal(1, MlDatasetService.EncodePattern("Doji"));
        Assert.Equal(10, MlDatasetService.EncodePattern("BearishMarubozu"));
        Assert.Equal(11, MlDatasetService.EncodePattern("BullishEngulfing"));
        Assert.Equal(24, MlDatasetService.EncodePattern("ThreeInsideDown"));
    }
}
