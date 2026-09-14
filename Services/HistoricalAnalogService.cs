using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public sealed class HistoricalAnalogService : IHistoricalAnalogService
{
    private static readonly int[] HorizonBars = [1, 3, 6];
    private static readonly HashSet<int> AllowedWindowSizes = [10, 15, 20, 25];
    private static readonly HashSet<string> AllowedSymbols = ["BTCUSDT", "ETHUSDT", "SOLUSDT"];
    private const string ShapeFeatureType = PatternVectorFeatureType.ReturnsShape;
    private readonly AppDbContext _db;
    private readonly ProductionTimeframePolicy _timeframePolicy;
    private readonly ILogger<HistoricalAnalogService> _logger;

    public HistoricalAnalogService(
        AppDbContext db,
        ProductionTimeframePolicy timeframePolicy,
        ILogger<HistoricalAnalogService> logger)
    {
        _db = db;
        _timeframePolicy = timeframePolicy;
        _logger = logger;
    }

    public async Task<HistoricalAnalogResponse> SearchAsync(
        HistoricalAnalogRequest request,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var symbol = request.Symbol.Trim().ToUpperInvariant();
        if (!AllowedSymbols.Contains(symbol))
            throw new ArgumentException("symbol must be BTCUSDT, ETHUSDT, or SOLUSDT.", nameof(request.Symbol));

        var timeframe = _timeframePolicy.EnsureActive(request.Timeframe);
        if (!AllowedWindowSizes.Contains(request.WindowSize))
            throw new ArgumentException("windowSize must be one of 10, 15, 20, or 25.", nameof(request.WindowSize));
        if (!double.IsFinite(request.RoundTripCostPct) || request.RoundTripCostPct < 0 || request.RoundTripCostPct > 5)
            throw new ArgumentException("roundTripCostPct must be between 0 and 5.", nameof(request.RoundTripCostPct));
        if (!double.IsFinite(request.AtrMultiplier) || request.AtrMultiplier < 0 || request.AtrMultiplier > 5)
            throw new ArgumentException("atrMultiplier must be between 0 and 5.", nameof(request.AtrMultiplier));

        var intervalMs = Timeframes.IntervalToMs(timeframe);
        if (intervalMs <= 0) throw new ArgumentException("Unsupported timeframe.", nameof(request.Timeframe));

        var lookbackBars = Math.Clamp(request.LookbackBars, 100, 100_000);
        var neighborCount = Math.Clamp(request.NeighborCount, 1, 200);
        var page = Math.Clamp(request.Page, 1, 1000);
        var pageSize = Math.Clamp(request.PageSize, 1, 50);
        var exclusionBars = request.WindowSize + HorizonBars[^1];

        var response = new HistoricalAnalogResponse
        {
            RequestId = requestId,
            Symbol = symbol,
            Timeframe = timeframe,
            IntervalMs = intervalMs,
            WindowSize = request.WindowSize,
            LookbackBars = lookbackBars,
            NeighborCount = neighborCount,
            Page = page,
            PageSize = pageSize,
            ExclusionBars = exclusionBars,
            RoundTripCostPct = request.RoundTripCostPct,
            AtrMultiplier = request.AtrMultiplier
        };

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var requestedRows = lookbackBars + request.WindowSize + HorizonBars[^1] + 14;
        var rowsDescending = await _db.Klines.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.CloseTimeMs <= nowMs)
            .OrderByDescending(x => x.OpenTimeMs)
            .Take(requestedRows)
            .Select(x => new KlineDto
            {
                OpenTimeMs = x.OpenTimeMs,
                CloseTimeMs = x.CloseTimeMs,
                Open = x.Open,
                High = x.High,
                Low = x.Low,
                Close = x.Close,
                Volume = x.Volume
            })
            .ToListAsync(cancellationToken);
        rowsDescending.Reverse();
        var rows = rowsDescending;

        var minimumRows = (request.WindowSize * 2) + HorizonBars[^1];
        if (rows.Count < minimumRows)
            return WithUnavailableValidation(response, $"Cần ít nhất {minimumRows} nến đã đóng và liên tục.");

        var queryStartIndex = rows.Count - request.WindowSize;
        if (!IsContiguous(rows, queryStartIndex, request.WindowSize, intervalMs))
            return WithUnavailableValidation(response, "Cửa sổ truy vấn hiện tại có nến bị thiếu.");

        var queryVector = BuildShapeVector(rows, queryStartIndex, request.WindowSize);
        if (queryVector is null)
            return WithUnavailableValidation(response, "Không thể chuẩn hóa cửa sổ truy vấn hiện tại.");
        var queryNorm = VectorNorm(queryVector);
        var queryStartMs = rows[queryStartIndex].OpenTimeMs;
        var queryEndMs = rows[^1].OpenTimeMs;

        var features = await LoadContextFeaturesAsync(symbol, timeframe, rows[0].OpenTimeMs, queryEndMs, cancellationToken);
        features.TryGetValue(queryEndMs, out var queryFeature);
        var queryContext = ContextValues(queryFeature);
        response.Query = new HistoricalAnalogQueryDto
        {
            StartTimeMs = queryStartMs,
            EndTimeMs = queryEndMs,
            Ohlc = rows.Skip(queryStartIndex).Take(request.WindowSize).Select(ToOhlc).ToList(),
            Context = new HistoricalAnalogContextDto
            {
                Values = queryContext.Raw,
                AvailableFeatureCount = queryContext.Normalized.Count
            }
        };

        var candidates = new List<Candidate>();
        var latestCandidateStart = queryStartIndex - request.WindowSize - HorizonBars[^1];
        for (var start = 0; start <= latestCandidateStart; start++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsContiguous(rows, start, exclusionBars, intervalMs)) continue;

            var endIndex = start + request.WindowSize - 1;
            var futureEndIndex = endIndex + HorizonBars[^1];
            if (rows[futureEndIndex].OpenTimeMs >= queryStartMs) continue;

            var candidateVector = BuildShapeVector(rows, start, request.WindowSize);
            if (candidateVector is null) continue;
            var atr14Pct = GetAtr14Pct(rows, endIndex, features.GetValueOrDefault(rows[endIndex].OpenTimeMs));
            if (atr14Pct is null) continue;

            var shapeSimilarity = PatternVectorSimilarity.Cosine(
                queryVector, queryNorm, candidateVector, VectorNorm(candidateVector));
            var candidateContext = ContextValues(features.GetValueOrDefault(rows[endIndex].OpenTimeMs));
            var (contextSimilarity, comparableCount) = CompareContexts(queryContext.Normalized, candidateContext.Normalized);
            var thresholdPct = Math.Max(request.RoundTripCostPct, request.AtrMultiplier * atr14Pct.Value);

            candidates.Add(new Candidate(
                start,
                endIndex,
                futureEndIndex,
                shapeSimilarity,
                contextSimilarity,
                comparableCount,
                atr14Pct.Value,
                thresholdPct));
        }

        response.RawCandidateCount = candidates.Count;
        var ranked = candidates
            .OrderByDescending(x => x.ShapeSimilarity)
            .ThenByDescending(x => x.EndIndex)
            .ToList();

        // Greedy exclusion in similarity order. Marking end-index ranges makes the
        // non-overlap rule deterministic without an O(n^2) scan.
        var blockedEndIndices = new bool[rows.Count];
        var independent = new List<Candidate>();
        foreach (var candidate in ranked)
        {
            if (blockedEndIndices[candidate.EndIndex]) continue;
            independent.Add(candidate);
            var from = Math.Max(0, candidate.EndIndex - exclusionBars + 1);
            var to = Math.Min(rows.Count - 1, candidate.EndIndex + exclusionBars - 1);
            for (var index = from; index <= to; index++) blockedEndIndices[index] = true;
        }

        response.IndependentCandidateCount = independent.Count;
        var selected = independent.Take(neighborCount).ToList();
        response.EffectiveSampleCount = selected.Count;
        response.Total = selected.Count;

        var allItems = selected.Select((candidate, index) =>
            BuildItem(candidate, index + 1, rows, request.WindowSize)).ToList();
        response.Summaries = BuildSummaries(allItems);
        response.Items = allItems.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        _logger.LogInformation(
            "historical_analog_search_done requestId={RequestId} timeframe={Timeframe} windowSize={WindowSize} rawCandidates={RawCandidates} independentCandidates={IndependentCandidates} selected={Selected} latencyMs={LatencyMs}",
            requestId, timeframe, request.WindowSize, response.RawCandidateCount,
            response.IndependentCandidateCount, response.EffectiveSampleCount,
            (int)(DateTime.UtcNow - startedAt).TotalMilliseconds);

        return response;
    }

    private static HistoricalAnalogResponse WithUnavailableValidation(HistoricalAnalogResponse response, string reason)
    {
        response.Validation.Status = "unavailable";
        response.Validation.Reason = reason;
        return response;
    }

    private async Task<Dictionary<long, MlFeatureStore>> LoadContextFeaturesAsync(
        string symbol, string timeframe, long fromMs, long toMs, CancellationToken cancellationToken)
    {
        var items = await _db.MlFeatureStores.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.OpenTimeMs >= fromMs && x.OpenTimeMs <= toMs)
            .ToListAsync(cancellationToken);
        return items.GroupBy(x => x.OpenTimeMs).ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.UpdatedAtUtc).First());
    }

    private static float[]? BuildShapeVector(IReadOnlyList<KlineDto> rows, int start, int count) =>
        WindowVectorIndexer.BuildVector(rows, start, count, ShapeFeatureType);

    private static float VectorNorm(IEnumerable<float> values) =>
        (float)Math.Sqrt(values.Sum(x => x * x));

    private static bool IsContiguous(
        IReadOnlyList<KlineDto> rows, int start, int count, long intervalMs)
    {
        if (start < 0 || count <= 0 || start + count > rows.Count) return false;
        for (var i = start + 1; i < start + count; i++)
        {
            if (rows[i].OpenTimeMs - rows[i - 1].OpenTimeMs != intervalMs) return false;
        }
        return true;
    }

    private static double? GetAtr14Pct(
        IReadOnlyList<KlineDto> rows, int endIndex, MlFeatureStore? feature)
    {
        if (feature?.Atr14Pct is > 0 and var stored && double.IsFinite(stored)) return stored;
        if (endIndex < 14 || rows[endIndex].Close <= 0) return null;

        double totalTrueRange = 0;
        for (var i = endIndex - 13; i <= endIndex; i++)
        {
            var high = (double)rows[i].High;
            var low = (double)rows[i].Low;
            var previousClose = (double)rows[i - 1].Close;
            totalTrueRange += Math.Max(high - low, Math.Max(Math.Abs(high - previousClose), Math.Abs(low - previousClose)));
        }
        return (totalTrueRange / 14.0) / (double)rows[endIndex].Close * 100.0;
    }

    private static HistoricalAnalogItemDto BuildItem(
        Candidate candidate,
        int rank,
        IReadOnlyList<KlineDto> rows,
        int windowSize)
    {
        var baseClose = rows[candidate.EndIndex].Close;
        var outcomes = HorizonBars.Select(barsAhead =>
        {
            var target = rows[candidate.EndIndex + barsAhead];
            var returnPct = baseClose == 0 ? 0 : (double)((target.Close - baseClose) / baseClose * 100m);
            return new HistoricalAnalogOutcomeDto
            {
                BarsAhead = barsAhead,
                TargetOpenTimeMs = target.OpenTimeMs,
                TargetClose = target.Close,
                ReturnPct = returnPct,
                ThresholdPct = candidate.ThresholdPct,
                Direction = returnPct > candidate.ThresholdPct ? 1 : returnPct < -candidate.ThresholdPct ? -1 : 0
            };
        }).ToList();

        return new HistoricalAnalogItemDto
        {
            Rank = rank,
            WindowId = $"w_{rows[candidate.StartIndex].OpenTimeMs}_{rows[candidate.EndIndex].OpenTimeMs}",
            StartTimeMs = rows[candidate.StartIndex].OpenTimeMs,
            EndTimeMs = rows[candidate.EndIndex].OpenTimeMs,
            FutureEndTimeMs = rows[candidate.FutureEndIndex].OpenTimeMs,
            ShapeSimilarity = candidate.ShapeSimilarity,
            ContextSimilarity = candidate.ContextSimilarity,
            ContextComparableFeatureCount = candidate.ContextComparableFeatureCount,
            Atr14Pct = candidate.Atr14Pct,
            ThresholdPct = candidate.ThresholdPct,
            Ohlc = rows.Skip(candidate.StartIndex).Take(windowSize).Select(ToOhlc).ToList(),
            FutureOhlc = rows.Skip(candidate.EndIndex + 1).Take(HorizonBars[^1]).Select(ToOhlc).ToList(),
            Outcomes = outcomes
        };
    }

    private static List<HistoricalAnalogSummaryDto> BuildSummaries(IReadOnlyList<HistoricalAnalogItemDto> items) =>
        HorizonBars.Select(barsAhead =>
        {
            var outcomes = items.Select(x => x.Outcomes.Single(y => y.BarsAhead == barsAhead)).ToList();
            var returns = outcomes.Select(x => x.ReturnPct).OrderBy(x => x).ToList();
            var up = outcomes.Count(x => x.Direction == 1);
            var down = outcomes.Count(x => x.Direction == -1);
            var neutral = outcomes.Count - up - down;
            return new HistoricalAnalogSummaryDto
            {
                BarsAhead = barsAhead,
                TotalSamples = outcomes.Count,
                UpCount = up,
                DownCount = down,
                NeutralCount = neutral,
                UpRate = Rate(up, outcomes.Count),
                DownRate = Rate(down, outcomes.Count),
                NeutralRate = Rate(neutral, outcomes.Count),
                AvgReturnPct = outcomes.Count == 0 ? 0 : outcomes.Average(x => x.ReturnPct),
                MedianReturnPct = Median(returns),
                DominantDirection = DominantDirection(up, down, neutral)
            };
        }).ToList();

    private static double Rate(int count, int total) => total == 0 ? 0 : count / (double)total;

    private static double Median(IReadOnlyList<double> ordered) => ordered.Count switch
    {
        0 => 0,
        var count when count % 2 == 1 => ordered[count / 2],
        var count => (ordered[(count / 2) - 1] + ordered[count / 2]) / 2.0
    };

    private static int? DominantDirection(int up, int down, int neutral)
    {
        var max = Math.Max(up, Math.Max(down, neutral));
        var winners = (up == max ? 1 : 0) + (down == max ? 1 : 0) + (neutral == max ? 1 : 0);
        if (max == 0 || winners != 1) return null;
        return up == max ? 1 : down == max ? -1 : 0;
    }

    private static ContextData ContextValues(MlFeatureStore? feature)
    {
        var raw = new Dictionary<string, double>(StringComparer.Ordinal);
        var normalized = new Dictionary<string, double>(StringComparer.Ordinal);
        if (feature is null) return new ContextData(raw, normalized);

        Add("rsi14", feature.Rsi14, x => Math.Clamp(x / 100.0, 0, 1));
        Add("macdHistogramNorm", feature.MacdHistogramNorm, Math.Tanh);
        Add("ema50Dist", feature.Ema50Dist, x => Math.Tanh(x / 5.0));
        Add("sma200Dist", feature.Sma200Dist, x => Math.Tanh(x / 10.0));
        Add("bollingerWidth", feature.BollingerWidth, x => Math.Tanh(x / 10.0));
        Add("bollingerPosition", feature.BollingerPosition, Math.Tanh);
        Add("atr14Pct", feature.Atr14Pct, x => Math.Tanh(x / 5.0));
        Add("volumeZscore", feature.VolumeZscore, x => Math.Tanh(x / 3.0));
        Add("takerBuyRatio", feature.TakerBuyRatio, x => Math.Clamp(x, 0, 1));
        Add("fundingRateNorm", feature.FundingRateNorm, Math.Tanh);
        Add("oiChangePct24", feature.OiChangePct24, x => Math.Tanh(x / 10.0));
        return new ContextData(raw, normalized);

        void Add(string name, double? value, Func<double, double> normalize)
        {
            if (value is not { } number || !double.IsFinite(number)) return;
            raw[name] = number;
            normalized[name] = normalize(number);
        }
    }

    private static (double? Similarity, int ComparableCount) CompareContexts(
        IReadOnlyDictionary<string, double> query,
        IReadOnlyDictionary<string, double> candidate)
    {
        var common = query.Keys.Where(candidate.ContainsKey).ToList();
        if (common.Count < 4) return (null, common.Count);
        var meanSquaredDifference = common.Average(key => Math.Pow(query[key] - candidate[key], 2));
        return (Math.Clamp(1.0 - (Math.Sqrt(meanSquaredDifference) / 2.0), 0, 1), common.Count);
    }

    private static ArchetypeOccurrenceOhlcDto ToOhlc(KlineDto item) => new()
    {
        OpenTimeMs = item.OpenTimeMs,
        Open = item.Open,
        High = item.High,
        Low = item.Low,
        Close = item.Close,
        Volume = item.Volume
    };

    private sealed record ContextData(Dictionary<string, double> Raw, Dictionary<string, double> Normalized);

    private sealed record Candidate(
        int StartIndex,
        int EndIndex,
        int FutureEndIndex,
        double ShapeSimilarity,
        double? ContextSimilarity,
        int ContextComparableFeatureCount,
        double Atr14Pct,
        double ThresholdPct);

}
