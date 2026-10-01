using Backend.Data;
using Backend.Services.Models;

namespace Backend.Services;

public interface ITechnicalReplayLayerService
{
    TechnicalReplayLayersDto Build(
        long requestedAsOfTimeMs,
        IReadOnlyList<Kline> contiguousRows,
        long? replayWindowStartTimeMs);

    IReadOnlyList<TechnicalLayerCoverageDto> BuildCoverage(
        TechnicalReplayLayersDto layers,
        bool hasGapBoundary);
}

public sealed class TechnicalReplayLayerService : ITechnicalReplayLayerService
{
    internal const string IndicatorsVersion = "finite-window-indicator-events-v1";
    internal const string CandlePatternsVersion = "causal-candle-shapes-v1";
    internal const string VolumeAnomalyVersion = "prior-volume-sma-ratio-v1";
    internal const string MarketRegimeVersion = "six-close-trend-range-volatility-v1";
    internal const string FibonacciVersion = "confirmed-alternating-pivot-leg-v1";
    internal const string VolumeProfileVersion = "rolling-typical-price-volume-profile-v1";
    internal const string ConfluenceVersion = "same-close-causal-event-vote-v1";
    private readonly ITechnicalModuleContractProvider contract;

    public TechnicalReplayLayerService(ITechnicalModuleContractProvider contract)
    {
        this.contract = contract;
        IReadOnlyDictionary<string, string> expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["technicalIndicators"] = IndicatorsVersion,
            ["candlePatterns"] = CandlePatternsVersion,
            ["volumeAnomaly"] = VolumeAnomalyVersion,
            ["marketRegime"] = MarketRegimeVersion,
            ["fibonacci"] = FibonacciVersion,
            ["volumeProfile"] = VolumeProfileVersion,
            ["confluence"] = ConfluenceVersion
        };
        foreach (var item in expected)
        {
            var declared = contract.GetCalculationVersion(item.Key);
            if (!string.Equals(declared, item.Value, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Technical module '{item.Key}' calculation version mismatch: contract='{declared}', backend='{item.Value}'.");
        }
    }

    public TechnicalReplayLayersDto Build(
        long requestedAsOfTimeMs,
        IReadOnlyList<Kline> contiguousRows,
        long? replayWindowStartTimeMs)
    {
        var indicators = BuildIndicators(requestedAsOfTimeMs, contiguousRows);
        var patterns = BuildCandlePatterns(requestedAsOfTimeMs, contiguousRows, replayWindowStartTimeMs);
        var volume = BuildVolumeAnomaly(requestedAsOfTimeMs, contiguousRows);
        var regime = BuildMarketRegime(requestedAsOfTimeMs, contiguousRows);
        var fibonacci = BuildFibonacci(requestedAsOfTimeMs, contiguousRows);
        var volumeProfile = BuildVolumeProfile(requestedAsOfTimeMs, contiguousRows);
        var confluence = BuildConfluence(requestedAsOfTimeMs, contiguousRows,
            indicators, patterns, volume, regime, fibonacci, volumeProfile);

        return new TechnicalReplayLayersDto
        {
            Indicators = indicators,
            CandlePatterns = patterns,
            VolumeAnomaly = volume,
            MarketRegime = regime,
            Fibonacci = fibonacci,
            VolumeProfile = volumeProfile,
            Confluence = confluence
        };
    }

    public IReadOnlyList<TechnicalLayerCoverageDto> BuildCoverage(
        TechnicalReplayLayersDto layers,
        bool hasGapBoundary) =>
    [
        Coverage(layers.Indicators, hasGapBoundary),
        Coverage(layers.CandlePatterns, hasGapBoundary),
        Coverage(layers.VolumeAnomaly, hasGapBoundary),
        Coverage(layers.MarketRegime, hasGapBoundary),
        Coverage(layers.Fibonacci, hasGapBoundary),
        Coverage(layers.VolumeProfile, hasGapBoundary),
        Coverage(layers.Confluence, hasGapBoundary)
    ];

    private TechnicalLayerEnvelopeDto<TechnicalIndicatorReplayDto> BuildIndicators(
        long requestedAsOf, IReadOnlyList<Kline> rows)
    {
        const int required = 61;
        if (rows.Count < required)
            return Unavailable<TechnicalIndicatorReplayDto>("technicalIndicators", IndicatorsVersion,
                requestedAsOf, rows, required, $"Requires {required} contiguous finalized candles; found {rows.Count}.");

        var current = IndicatorState(rows, rows.Count - 1);
        var previous = IndicatorState(rows, rows.Count - 2);
        var events = new List<TechnicalIndicatorEventDto>();
        if (previous.FastEma <= previous.SlowEma && current.FastEma > current.SlowEma)
            events.Add(new("EMA_BULL_CROSS", 1, (double)(current.FastEma - current.SlowEma)));
        if (previous.FastEma >= previous.SlowEma && current.FastEma < current.SlowEma)
            events.Add(new("EMA_BEAR_CROSS", -1, (double)(current.FastEma - current.SlowEma)));
        AddLevelCross(events, previous.Rsi, current.Rsi, 30, "RSI_ENTER_OVERSOLD", 1, "RSI_EXIT_OVERSOLD", 1);
        AddLevelCross(events, previous.Rsi, current.Rsi, 70, "RSI_EXIT_OVERBOUGHT", -1, "RSI_ENTER_OVERBOUGHT", -1);
        if (previous.Close <= previous.Sma50 && current.Close > current.Sma50)
            events.Add(new("CLOSE_ABOVE_SMA50", 1, (double)(current.Close - current.Sma50)));
        if (previous.Close >= previous.Sma50 && current.Close < current.Sma50)
            events.Add(new("CLOSE_BELOW_SMA50", -1, (double)(current.Close - current.Sma50)));

        var last = rows[^1];
        return Available("technicalIndicators", IndicatorsVersion, requestedAsOf, rows.TakeLast(required).ToArray(), required,
            last.CloseTimeMs, new TechnicalIndicatorReplayDto
            {
                OpenTimeMs = last.OpenTimeMs,
                AvailableTimeMs = last.CloseTimeMs,
                Rsi14 = current.Rsi,
                Ema12 = current.FastEma,
                Ema26 = current.SlowEma,
                Sma50 = current.Sma50,
                Events = events
            }, ["Finite-window EMA is descriptive and intentionally differs from legacy full-history EMA rows."]);
    }

    private TechnicalLayerEnvelopeDto<CandlePatternReplayDto> BuildCandlePatterns(
        long requestedAsOf, IReadOnlyList<Kline> rows, long? replayWindowStart)
    {
        if (rows.Count == 0)
            return Unavailable<CandlePatternReplayDto>("candlePatterns", CandlePatternsVersion,
                requestedAsOf, rows, 2, "No finalized contiguous candles are available.");

        var events = new List<CandlePatternEventDto>();
        for (var i = 0; i < rows.Count; i++)
        {
            var candle = rows[i];
            if (replayWindowStart.HasValue && candle.OpenTimeMs < replayWindowStart.Value) continue;
            var range = candle.High - candle.Low;
            if (range > 0)
            {
                var body = Math.Abs(candle.Close - candle.Open);
                var epsilonBody = Math.Max(body, range * 0.000001m);
                var upper = candle.High - Math.Max(candle.Open, candle.Close);
                var lower = Math.Min(candle.Open, candle.Close) - candle.Low;
                if (body / range <= 0.1m)
                    events.Add(Pattern("DOJI", "Single", candle, candle.CloseTimeMs, [candle.OpenTimeMs]));
                if (lower >= 2m * epsilonBody && upper <= epsilonBody)
                    events.Add(Pattern("HAMMER_SHAPE", "Single", candle, candle.CloseTimeMs, [candle.OpenTimeMs]));
                if (upper >= 2m * epsilonBody && lower <= epsilonBody)
                    events.Add(Pattern("SHOOTING_STAR_SHAPE", "Single", candle, candle.CloseTimeMs, [candle.OpenTimeMs]));
            }

            if (i == 0) continue;
            var previous = rows[i - 1];
            if (previous.Close < previous.Open && candle.Close > candle.Open
                && candle.Open <= previous.Close && candle.Close >= previous.Open)
                events.Add(Pattern("BULLISH_ENGULFING", "Double", previous, candle.CloseTimeMs,
                    [previous.OpenTimeMs, candle.OpenTimeMs]));
            if (previous.Close > previous.Open && candle.Close < candle.Open
                && candle.Open >= previous.Close && candle.Close <= previous.Open)
                events.Add(Pattern("BEARISH_ENGULFING", "Double", previous, candle.CloseTimeMs,
                    [previous.OpenTimeMs, candle.OpenTimeMs]));
        }

        var firstVisible = replayWindowStart.HasValue
            ? Math.Max(0, rows.ToList().FindIndex(x => x.OpenTimeMs >= replayWindowStart.Value) - 1)
            : 0;
        var sourceRows = rows.Skip(firstVisible).ToArray();
        return Available("candlePatterns", CandlePatternsVersion, requestedAsOf, sourceRows, 2,
            rows[^1].CloseTimeMs, new CandlePatternReplayDto { Events = events },
            ["Shape events do not assert reversal probability or economic value."]);
    }

    private TechnicalLayerEnvelopeDto<VolumeAnomalyReplayDto> BuildVolumeAnomaly(
        long requestedAsOf, IReadOnlyList<Kline> rows)
    {
        const int required = 21;
        if (rows.Count < required)
            return Unavailable<VolumeAnomalyReplayDto>("volumeAnomaly", VolumeAnomalyVersion,
                requestedAsOf, rows, required, $"Requires 20 prior candles plus the decision candle; found {rows.Count}.");
        var last = rows[^1];
        var prior = rows.Skip(rows.Count - required).Take(20).ToArray();
        var mean = prior.Average(x => x.Volume);
        if (mean <= 0)
            return Unavailable<VolumeAnomalyReplayDto>("volumeAnomaly", VolumeAnomalyVersion,
                requestedAsOf, rows.TakeLast(required).ToArray(), required,
                "Prior 20-candle mean volume must be positive.");
        var ratio = mean > 0 ? (double)(last.Volume / mean) : 0;
        var previous = prior[^1].Volume;
        var max10 = prior.TakeLast(10).Max(x => x.Volume);
        var events = new List<string>();
        if (ratio >= 1.5) events.Add("VOLUME_ANOMALY_1_5X");
        if (ratio >= 2.0) events.Add("VOLUME_ANOMALY_2_0X");
        var recent3 = rows.TakeLast(3).Select(x => x.Volume).ToArray();
        var trend = recent3[2] > recent3[1] && recent3[1] > recent3[0] ? "increasing"
            : recent3[2] < recent3[1] && recent3[1] < recent3[0] ? "decreasing" : "normal";

        return Available("volumeAnomaly", VolumeAnomalyVersion, requestedAsOf, rows.TakeLast(required).ToArray(), required,
            last.CloseTimeMs, new VolumeAnomalyReplayDto
            {
                OpenTimeMs = last.OpenTimeMs,
                AvailableTimeMs = last.CloseTimeMs,
                Volume = last.Volume,
                VolumeSma20 = mean,
                VolumeAnomalyRatio = ratio,
                VolumeVsPrevious = previous > 0 ? (double)(last.Volume / previous) : 0,
                VolumeVsMax10 = max10 > 0 ? (double)(last.Volume / max10) : 0,
                VolumeTrend = trend,
                TriggeredEvents = events
            });
    }

    private TechnicalLayerEnvelopeDto<MarketRegimeReplayDto> BuildMarketRegime(
        long requestedAsOf, IReadOnlyList<Kline> rows)
    {
        const int required = 12;
        if (rows.Count < required)
            return Unavailable<MarketRegimeReplayDto>("marketRegime", MarketRegimeVersion,
                requestedAsOf, rows, required, "Requires at least 10 prior true-range ratios and close changes.");
        var current = RegimeState(rows, rows.Count - 1);
        if (current.Volatility == "insufficient_history")
            return Unavailable<MarketRegimeReplayDto>("marketRegime", MarketRegimeVersion,
                requestedAsOf, rows, required, "Prior volatility history is insufficient.");
        var previous = RegimeState(rows, rows.Count - 2);
        var changed = previous.Volatility != "insufficient_history"
            && (current.Trend != previous.Trend || current.Volatility != previous.Volatility);
        var eventType = changed
            ? current.Trend == "up" ? $"REGIME_BULL_{current.Volatility.ToUpperInvariant()}"
            : current.Trend == "down" ? $"REGIME_BEAR_{current.Volatility.ToUpperInvariant()}"
            : $"REGIME_SIDEWAYS_{current.Volatility.ToUpperInvariant()}"
            : null;
        var last = rows[^1];
        return Available("marketRegime", MarketRegimeVersion, requestedAsOf, rows.TakeLast(Math.Min(rows.Count, 22)).ToArray(), required,
            last.CloseTimeMs, new MarketRegimeReplayDto
            {
                OpenTimeMs = last.OpenTimeMs,
                AvailableTimeMs = last.CloseTimeMs,
                RegimeType = $"{current.Trend}_{current.Volatility}",
                Trend = current.Trend,
                Volatility = current.Volatility,
                EventType = eventType,
                UpChanges = current.Up,
                DownChanges = current.Down,
                CurrentTrueRangePct = current.CurrentRange,
                PriorMedianTrueRangePct = current.PriorMedian,
                RangeRatio = current.PriorMedian > 0 ? current.CurrentRange / current.PriorMedian : 0
            }, ["Causal regime reconstruction is not comparable to legacy ADX/ATR/Bollinger MarketRegimes rows."]);
    }

    private TechnicalLayerEnvelopeDto<FibonacciReplayDto> BuildFibonacci(
        long requestedAsOf, IReadOnlyList<Kline> rows)
    {
        const int required = 5;
        var legs = FibonacciLegs(rows);
        if (legs.Count == 0)
            return Unavailable<FibonacciReplayDto>("fibonacci", FibonacciVersion, requestedAsOf, rows,
                required, "No confirmed alternating high/low pivot leg is available.");
        var leg = legs[^1];
        var range = leg.High.Price - leg.Low.Price;
        var sourceStart = Math.Max(0, leg.Start.Index - 2);
        var sourceEnd = Math.Min(rows.Count - 1, leg.End.Index + 2);
        var sourceRows = rows.Skip(sourceStart).Take(sourceEnd - sourceStart + 1).ToArray();
        double[] ratios = [0.236, 0.382, 0.5, 0.618, 0.786];
        var levels = ratios.Select(r => new FibonacciLevelDto(r,
            leg.Direction == "bull" ? leg.High.Price - range * r : leg.Low.Price + range * r)).ToArray();
        return Available("fibonacci", FibonacciVersion, requestedAsOf, sourceRows, required,
            leg.AvailableTimeMs, new FibonacciReplayDto
            {
                EventType = leg.Direction == "bull" ? "FIBONACCI_LEG_BULL" : "FIBONACCI_LEG_BEAR",
                AvailableTimeMs = leg.AvailableTimeMs,
                Direction = leg.Direction,
                AnchorStartTimeMs = leg.Start.TimeMs,
                AnchorEndTimeMs = leg.End.TimeMs,
                AnchorLow = leg.Low.Price,
                AnchorHigh = leg.High.Price,
                Levels = levels
            }, ["Levels describe a confirmed historical leg and are not price targets."],
            effectiveAsOf: rows[^1].CloseTimeMs);
    }

    private TechnicalLayerEnvelopeDto<VolumeProfileReplayDto> BuildVolumeProfile(
        long requestedAsOf, IReadOnlyList<Kline> rows)
    {
        const int required = 100;
        if (rows.Count < required)
            return Unavailable<VolumeProfileReplayDto>("volumeProfile", VolumeProfileVersion,
                requestedAsOf, rows, required, $"Requires {required} contiguous finalized candles; found {rows.Count}.");
        var currentWindow = rows.TakeLast(required).ToArray();
        if (currentWindow.Sum(x => x.Volume) <= 0 || currentWindow.Max(x => x.High) <= currentWindow.Min(x => x.Low))
            return Unavailable<VolumeProfileReplayDto>("volumeProfile", VolumeProfileVersion,
                requestedAsOf, currentWindow, required, "Volume profile requires positive volume and a non-zero price range.");
        var current = Profile(rows, rows.Count - 1);
        var events = new List<string>();
        if (rows.Count >= required + 1)
        {
            var previousWindow = rows.Skip(rows.Count - required - 1).Take(required).ToArray();
            if (previousWindow.Sum(x => x.Volume) > 0 && previousWindow.Max(x => x.High) > previousWindow.Min(x => x.Low))
            {
                var previous = Profile(rows, rows.Count - 2);
            var previousClose = (double)rows[^2].Close;
            var currentClose = (double)rows[^1].Close;
            AddProfileCross(events, previousClose, currentClose, previous.PocPrice, current.PocPrice, "POC");
            AddProfileCross(events, previousClose, currentClose, previous.VahPrice, current.VahPrice, "VAH");
            AddProfileCross(events, previousClose, currentClose, previous.ValPrice, current.ValPrice, "VAL");
            }
        }
        current.Events = events;
        return Available("volumeProfile", VolumeProfileVersion, requestedAsOf,
            rows.TakeLast(Math.Min(rows.Count, required + 1)).ToArray(), required,
            rows[^1].CloseTimeMs, current,
            ["OHLCV estimate: full candle volume is allocated to typical price; this is not observed price-by-volume."]);
    }

    private TechnicalLayerEnvelopeDto<ConfluenceReplayDto> BuildConfluence(
        long requestedAsOf,
        IReadOnlyList<Kline> rows,
        TechnicalLayerEnvelopeDto<TechnicalIndicatorReplayDto> indicators,
        TechnicalLayerEnvelopeDto<CandlePatternReplayDto> patterns,
        TechnicalLayerEnvelopeDto<VolumeAnomalyReplayDto> volume,
        TechnicalLayerEnvelopeDto<MarketRegimeReplayDto> regime,
        TechnicalLayerEnvelopeDto<FibonacciReplayDto> fibonacci,
        TechnicalLayerEnvelopeDto<VolumeProfileReplayDto> profile)
    {
        if (rows.Count == 0)
            return Unavailable<ConfluenceReplayDto>("confluence", ConfluenceVersion, requestedAsOf,
                rows, 2, "No finalized decision candle is available.");
        var close = rows[^1].CloseTimeMs;
        var moduleEvents = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["technicalIndicators"] = indicators.Payload?.AvailableTimeMs == close
                ? indicators.Payload.Events.Select(x => x.EventType).ToArray() : [],
            ["candlePatterns"] = patterns.Payload?.Events.Where(x => x.AvailableTimeMs == close)
                .Select(x => x.PatternType).ToArray() ?? [],
            ["volumeAnomaly"] = volume.Payload?.AvailableTimeMs == close ? volume.Payload.TriggeredEvents : [],
            ["marketRegime"] = regime.Payload?.AvailableTimeMs == close && regime.Payload.EventType is not null
                ? [regime.Payload.EventType] : [],
            ["fibonacci"] = fibonacci.Payload?.AvailableTimeMs == close ? [fibonacci.Payload.EventType] : [],
            ["volumeProfile"] = profile.Lineage.AvailableTimeMs == close ? profile.Payload?.Events ?? [] : []
        };
        var votes = moduleEvents.Select(x => ModuleVote(x.Key, x.Value, close)).Where(x => x.Vote != 0).ToArray();
        var bull = votes.Count(x => x.Vote > 0);
        var bear = votes.Count(x => x.Vote < 0);
        var aligned = Math.Max(bull, bear);
        var hasConflict = bull >= 2 && bear >= 2;
        var direction = hasConflict ? "Conflict" : bull >= 2 ? "Bullish" : bear >= 2 ? "Bearish" : "Neutral";
        var score = votes.Length == 0 ? 0 : votes.Average(x => x.Vote);
        var payload = new ConfluenceReplayDto
        {
            AvailableTimeMs = close,
            EventType = direction == "Bullish" ? "CONFLUENCE_BULL_2PLUS"
                : direction == "Bearish" ? "CONFLUENCE_BEAR_2PLUS" : null,
            TriggeredEvents = bull >= 2 && bear >= 2
                ? ["CONFLUENCE_BULL_2PLUS", "CONFLUENCE_BEAR_2PLUS"]
                : bull >= 2 ? ["CONFLUENCE_BULL_2PLUS"]
                : bear >= 2 ? ["CONFLUENCE_BEAR_2PLUS"] : [],
            Score = score,
            OverallDirection = direction,
            HasConflict = hasConflict,
            AlignedDirectionalModules = aligned,
            ModuleVotes = votes
        };
        var availability = aligned >= 2 ? TechnicalLayerAvailability.Available : TechnicalLayerAvailability.Partial;
        var sourceVotes = bull >= 2 && bear >= 2 ? votes
            : bull >= 2 ? votes.Where(x => x.Vote > 0).ToArray()
            : bear >= 2 ? votes.Where(x => x.Vote < 0).ToArray()
            : votes;
        var sourceStart = sourceVotes.Select(x => moduleEvents[x.LayerKey].Count == 0 ? (long?)null : x.LayerKey switch
            {
                "technicalIndicators" => indicators.Lineage.SourceStartTimeMs,
                "candlePatterns" => patterns.Lineage.SourceStartTimeMs,
                "volumeAnomaly" => volume.Lineage.SourceStartTimeMs,
                "marketRegime" => regime.Lineage.SourceStartTimeMs,
                "fibonacci" => fibonacci.Lineage.SourceStartTimeMs,
                "volumeProfile" => profile.Lineage.SourceStartTimeMs,
                _ => null
            }).Where(x => x.HasValue).Min();
        var sourceRows = sourceStart.HasValue
            ? rows.SkipWhile(x => x.OpenTimeMs < sourceStart.Value).ToArray()
            : rows.TakeLast(1).ToArray();
        return Envelope("confluence", ConfluenceVersion, availability, requestedAsOf, sourceRows, 2, close,
            payload, availability == TechnicalLayerAvailability.Partial
                ? "Fewer than two distinct modules align directionally at the same finalized close." : null,
            ["Descriptive same-close vote only; it is not a probability or predictive signal."]);
    }

    private TechnicalLayerEnvelopeDto<T> Available<T>(string key, string version, long asOf,
        IReadOnlyList<Kline> rows, int warmup, long availableAt, T payload,
        IReadOnlyList<string>? limitations = null, long? effectiveAsOf = null) =>
        Envelope(key, version, TechnicalLayerAvailability.Available, asOf, rows, warmup,
            availableAt, payload, null, limitations ?? [], effectiveAsOf);

    private TechnicalLayerEnvelopeDto<T> Unavailable<T>(string key, string version, long asOf,
        IReadOnlyList<Kline> rows, int warmup, string reason) =>
        Envelope<T>(key, version, TechnicalLayerAvailability.Unavailable, asOf, rows, warmup,
            null, default, reason, []);

    private TechnicalLayerEnvelopeDto<T> Envelope<T>(string key, string version, string availability,
        long asOf, IReadOnlyList<Kline> rows, int warmup, long? availableAt, T? payload,
        string? reason, IReadOnlyList<string> limitations, long? effectiveAsOf = null) => new()
    {
        LayerKey = key,
        Availability = availability,
        Lineage = new TechnicalLayerLineageDto
        {
            ModuleContractVersion = contract.ContractVersion,
            ModuleContractSha256 = contract.Sha256,
            Producer = "Backend.TechnicalReplayLayerService",
            CalculationVersion = version,
            RequestedAsOfTimeMs = asOf,
            EffectiveAsOfTimeMs = effectiveAsOf ?? (rows.Count == 0 ? null : rows[^1].CloseTimeMs),
            AvailableTimeMs = availableAt,
            SourceStartTimeMs = rows.Count == 0 ? null : rows[0].OpenTimeMs,
            SourceEndTimeMs = rows.Count == 0 ? null : rows[^1].CloseTimeMs,
            SourceCandleCount = rows.Count,
            RequiredWarmupBars = warmup,
            IsCausal = true,
            IsPersisted = false
        },
        UnavailableReason = reason,
        Limitations = limitations,
        Payload = payload
    };

    private static TechnicalLayerCoverageDto Coverage<T>(TechnicalLayerEnvelopeDto<T> layer,
        bool hasGapBoundary) => new()
    {
        LayerKey = layer.LayerKey,
        Availability = layer.Availability,
        SourceBars = layer.Lineage.SourceCandleCount,
        RequiredWarmupBars = layer.Lineage.RequiredWarmupBars,
        LatestAvailableTimeMs = layer.Lineage.AvailableTimeMs,
        HasGapBoundary = hasGapBoundary,
        CheckpointStatus = "not_started",
        StorageStatus = "on_demand_state",
        IsEventEnvelopeMaterializedAtAsOf = false
    };

    private static IndicatorPoint IndicatorState(IReadOnlyList<Kline> rows, int end)
    {
        var emaStart = Math.Max(0, end - 59);
        var closes = rows.Skip(emaStart).Take(end - emaStart + 1).Select(x => x.Close).ToArray();
        var fast = FiniteEma(closes, 12);
        var slow = FiniteEma(closes, 26);
        var rsiStart = end - 14;
        var gains = 0m; var losses = 0m;
        for (var i = rsiStart + 1; i <= end; i++)
        {
            var change = rows[i].Close - rows[i - 1].Close;
            if (change > 0) gains += change; else losses -= change;
        }
        var avgGain = gains / 14m; var avgLoss = losses / 14m;
        var rsi = avgLoss == 0 ? 100d : 100d - 100d / (1d + (double)(avgGain / avgLoss));
        var sma50 = rows.Skip(end - 49).Take(50).Average(x => x.Close);
        return new IndicatorPoint(rows[end].Close, fast, slow, rsi, sma50);
    }

    private static decimal FiniteEma(IReadOnlyList<decimal> closes, int period)
    {
        var alpha = 2m / (period + 1);
        var ema = closes[0];
        for (var i = 1; i < closes.Count; i++) ema = alpha * closes[i] + (1 - alpha) * ema;
        return ema;
    }

    private static void AddLevelCross(List<TechnicalIndicatorEventDto> events, double previous,
        double current, double level, string downwardName, int downwardDirection,
        string upwardName, int upwardDirection)
    {
        if (previous >= level && current < level) events.Add(new(downwardName, downwardDirection, current));
        if (previous <= level && current > level) events.Add(new(upwardName, upwardDirection, current));
    }

    private static CandlePatternEventDto Pattern(string type, string category, Kline origin,
        long availableAt, IReadOnlyList<long> source) => new()
    {
        PatternType = type, PatternCategory = category, TrendDirection = "shape_only",
        OriginTimeMs = origin.OpenTimeMs, AvailableTimeMs = availableAt, SourceOpenTimeMs = source
    };

    private static RegimePoint RegimeState(IReadOnlyList<Kline> rows, int end)
    {
        var changes = Math.Min(5, end);
        var up = 0; var down = 0;
        for (var i = end - changes + 1; i <= end; i++)
        {
            if (rows[i].Close > rows[i - 1].Close) up++;
            else if (rows[i].Close < rows[i - 1].Close) down++;
        }
        var trend = changes < 3 ? "sideways" : up >= 4 && down <= 1 ? "up"
            : down >= 4 && up <= 1 ? "down" : "sideways";
        var currentRange = TrueRangeRatio(rows, end);
        var prior = new List<double>();
        for (var i = Math.Max(0, end - 20); i < end; i++) prior.Add(TrueRangeRatio(rows, i));
        if (prior.Count < 10) return new(trend, "insufficient_history", up, down, currentRange, 0);
        prior.Sort();
        var median = prior.Count % 2 == 1 ? prior[prior.Count / 2]
            : (prior[prior.Count / 2 - 1] + prior[prior.Count / 2]) / 2;
        var volatility = currentRange >= 1.5 * median ? "high"
            : currentRange <= 0.67 * median ? "low" : "normal";
        return new(trend, volatility, up, down, currentRange, median);
    }

    private static double TrueRangeRatio(IReadOnlyList<Kline> rows, int index)
    {
        var row = rows[index];
        var range = row.High - row.Low;
        if (index > 0)
        {
            range = Math.Max(range, Math.Abs(row.High - rows[index - 1].Close));
            range = Math.Max(range, Math.Abs(row.Low - rows[index - 1].Close));
        }
        return row.Close == 0 ? 0 : (double)(range / row.Close);
    }

    private static List<FibonacciLeg> FibonacciLegs(IReadOnlyList<Kline> rows)
    {
        const int radius = 2;
        var legs = new List<FibonacciLeg>();
        PivotPoint? retained = null;
        for (var i = radius; i + radius < rows.Count; i++)
        {
            var high = true; var low = true;
            for (var j = i - radius; j <= i + radius; j++)
            {
                if (j == i) continue;
                high &= rows[i].High > rows[j].High;
                low &= rows[i].Low < rows[j].Low;
            }
            PivotPoint? current = high
                ? new("high", i, rows[i].OpenTimeMs, (double)rows[i].High, rows[i + radius].CloseTimeMs)
                : low ? new("low", i, rows[i].OpenTimeMs, (double)rows[i].Low, rows[i + radius].CloseTimeMs) : null;
            if (current is null) continue;
            if (retained is null || retained.Type == current.Type)
            {
                retained = current;
                continue;
            }
            var lowPoint = retained.Type == "low" ? retained : current;
            var highPoint = retained.Type == "high" ? retained : current;
            var direction = retained.Type == "low" ? "bull" : "bear";
            legs.Add(new(direction, retained, current, lowPoint, highPoint, current.AvailableTimeMs));
            retained = current;
        }
        return legs;
    }

    private static VolumeProfileReplayDto Profile(IReadOnlyList<Kline> rows, int end)
    {
        const int window = 100; const int binsCount = 24; const double valueArea = 0.7;
        var selected = rows.Skip(end - window + 1).Take(window).ToArray();
        var min = (double)selected.Min(x => x.Low); var max = (double)selected.Max(x => x.High);
        var width = (max - min) / binsCount;
        var bins = new double[binsCount];
        foreach (var row in selected)
        {
            var typical = (double)((row.High + row.Low + row.Close) / 3m);
            var index = width <= 0 ? 0 : Math.Clamp((int)Math.Floor((typical - min) / width), 0, binsCount - 1);
            bins[index] += (double)row.Volume;
        }
        var poc = Enumerable.Range(0, binsCount).OrderByDescending(i => bins[i]).ThenBy(i => i).First();
        var total = bins.Sum(); var cumulative = 0d; var valueBins = new HashSet<int>();
        foreach (var index in Enumerable.Range(0, binsCount).OrderByDescending(i => bins[i]).ThenBy(i => i))
        {
            valueBins.Add(index); cumulative += bins[index];
            if (cumulative >= valueArea * total) break;
        }
        double Mid(int i) => width <= 0 ? min : min + (i + 0.5) * width;
        var maxBin = bins.Max();
        return new VolumeProfileReplayDto
        {
            BinCount = binsCount, ValueAreaFraction = valueArea,
            WindowStartMs = selected[0].OpenTimeMs, WindowEndMs = selected[^1].CloseTimeMs,
            InputVolume = total, PocPrice = Mid(poc),
            VahPrice = valueBins.Count == 0 ? Mid(poc) : valueBins.Max(Mid),
            ValPrice = valueBins.Count == 0 ? Mid(poc) : valueBins.Min(Mid),
            Bins = bins.Select((volume, i) => new VolumeProfileBinDto(Mid(i), volume,
                maxBin > 0 ? volume / maxBin * 100 : 0, i == poc, valueBins.Contains(i))).ToArray()
        };
    }

    private static void AddProfileCross(List<string> events, double previousClose, double currentClose,
        double previousLevel, double currentLevel, string name)
    {
        if (previousClose < previousLevel && currentClose >= currentLevel)
            events.Add($"VOLUME_PROFILE_CLOSE_ABOVE_{name}");
        if (previousClose > previousLevel && currentClose <= currentLevel)
            events.Add($"VOLUME_PROFILE_CLOSE_BELOW_{name}");
    }

    private static ConfluenceModuleVoteDto ModuleVote(string key, IReadOnlyList<string> events, long close)
    {
        var bull = events.Any(x => x.Contains("BULL", StringComparison.Ordinal)
            || x.Contains("ABOVE", StringComparison.Ordinal)
            || x.Contains("EXIT_OVERSOLD", StringComparison.Ordinal)
            || x.Contains("ENTER_OVERSOLD", StringComparison.Ordinal));
        var bear = events.Any(x => x.Contains("BEAR", StringComparison.Ordinal)
            || x.Contains("BELOW", StringComparison.Ordinal)
            || x.Contains("EXIT_OVERBOUGHT", StringComparison.Ordinal)
            || x.Contains("ENTER_OVERBOUGHT", StringComparison.Ordinal));
        var vote = bull == bear ? 0 : bull ? 1 : -1;
        return new(key, vote, string.Join(',', events), close);
    }

    private sealed record IndicatorPoint(decimal Close, decimal FastEma, decimal SlowEma, double Rsi, decimal Sma50);
    private sealed record RegimePoint(string Trend, string Volatility, int Up, int Down, double CurrentRange, double PriorMedian);
    private sealed record PivotPoint(string Type, int Index, long TimeMs, double Price, long AvailableTimeMs);
    private sealed record FibonacciLeg(string Direction, PivotPoint Start, PivotPoint End,
        PivotPoint Low, PivotPoint High, long AvailableTimeMs);
}
