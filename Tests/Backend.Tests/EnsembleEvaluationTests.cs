using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Data;
using Backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Backend.Tests;

public class EnsembleEvaluationTests
{
    [Fact]
    public async Task EvaluatePredictions_UsesCandleAtHorizonInsteadOfLatestPrice()
    {
        await using var db = CreateDb();
        var predictionTime = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeMilliseconds();
        var horizonTime = predictionTime + 24 * 60 * 60 * 1000L;
        db.EnsemblePredictionRecords.Add(new EnsemblePredictionRecord
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            TimeMs = predictionTime,
            EntryPrice = 100,
            FinalDirection = "Bullish",
            EnsembleConfidence = 0.8,
            EvaluationStatus = "N"
        });
        db.Klines.AddRange(
            Candle(horizonTime, 90),
            Candle(horizonTime + 24 * 60 * 60 * 1000L, 200));
        await db.SaveChangesAsync();

        var service = new EnsembleService(db, new FakeBinanceKlinesService());
        var result = await service.EvaluatePredictionsAsync("BTCUSDT");

        var item = Assert.Single(result.Items);
        Assert.Equal("F", item.EvaluationStatus);
        Assert.Equal(90, item.ActualPrice24h);
        Assert.Equal(-10, item.ActualReturnPct);
        Assert.Equal(horizonTime, item.EvaluatedAtMs);
    }

    [Fact]
    public async Task EvaluatePredictions_ReevaluatesLegacyResultsWithPointInTimeCandle()
    {
        await using var db = CreateDb();
        var predictionTime = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeMilliseconds();
        var horizonTime = predictionTime + 24 * 60 * 60 * 1000L;
        db.EnsemblePredictionRecords.Add(new EnsemblePredictionRecord
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            TimeMs = predictionTime,
            EntryPrice = 100,
            FinalDirection = "Bullish",
            EnsembleConfidence = 0.8,
            EvaluationStatus = "T",
            ActualPrice24h = 200,
            ActualReturnPct = 100
        });
        db.Klines.Add(Candle(horizonTime, 90));
        await db.SaveChangesAsync();

        var service = new EnsembleService(db, new FakeBinanceKlinesService());
        var result = await service.EvaluatePredictionsAsync("BTCUSDT");

        var item = Assert.Single(result.Items);
        Assert.Equal("F", item.EvaluationStatus);
        Assert.Equal(90, item.ActualPrice24h);
        Assert.Equal(-10, item.ActualReturnPct);
        Assert.Equal(horizonTime, item.EvaluatedAtMs);
    }

    [Fact]
    public async Task GetSummary_IsReadOnlyAndLimitsReturnedItems()
    {
        await using var db = CreateDb();
        var oldTime = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeMilliseconds();
        for (var index = 0; index < 3; index++)
        {
            db.EnsemblePredictionRecords.Add(new EnsemblePredictionRecord
            {
                Symbol = "BTCUSDT",
                Timeframe = "1h",
                TimeMs = oldTime + index,
                EntryPrice = 100,
                FinalDirection = "Bullish",
                EnsembleConfidence = 0.8,
                EvaluationStatus = "N"
            });
        }
        await db.SaveChangesAsync();

        var service = new EnsembleService(db, new FakeBinanceKlinesService());
        var result = await service.GetPredictionEvaluationSummaryAsync("BTCUSDT", 2);

        Assert.Equal(3, result.TotalPredictions);
        Assert.Equal(3, result.PendingCount);
        Assert.Equal(2, result.Items.Count);
        Assert.All(await db.EnsemblePredictionRecords.ToListAsync(), item => Assert.Equal("N", item.EvaluationStatus));
    }

    public static TheoryData<bool, int> SummaryCases()
    {
        var cases = new TheoryData<bool, int>();
        foreach (var includeLegacy in new[] { false, true })
            foreach (var limit in new[] { 1, 2, 100, 500 })
                cases.Add(includeLegacy, limit);
        return cases;
    }

    [Theory]
    [MemberData(nameof(SummaryCases))]
    public async Task GetSummary_MatchesTheFullLoadImplementation(bool includeLegacy, int limit)
    {
        await using var db = CreateDb();
        db.EnsemblePredictionRecords.AddRange(MixedRecords());
        await db.SaveChangesAsync();

        var actual = await new EnsembleService(db, new FakeBinanceKlinesService())
            .GetPredictionEvaluationSummaryAsync("BTCUSDT", limit, includeLegacy);
        // The item cap moving from 500 to 200 is the one intended difference; the seed has fewer rows than either.
        var expected = await FullLoadSummaryAsync(db, "BTCUSDT", Math.Min(limit, 200), includeLegacy);

        Assert.Equal(Json(expected), Json(actual));
        // Worked by hand from MixedRecords, so the oracle cannot agree with the rewrite by both being empty.
        Assert.Equal(
            includeLegacy ? (12, 5, 4, 2, 3, 1, 1, 4, 2, 1, 1, 11L) : (9, 5, 2, 2, 4, 2, 1, 2, 2, 0, 0, 35L),
            (actual.TotalPredictions, actual.TrueCount, actual.FalseCount, actual.PendingCount,
                actual.CanonicalTrueCount, actual.CanonicalFalseCount, actual.CanonicalPendingCount,
                actual.ReevaluatedCount, actual.ReevaluatedTrueCount, actual.ReevaluatedFalseCount, actual.ReevaluatedPendingCount,
                actual.Items[0].Id));
        Assert.Equal(Math.Min(limit, includeLegacy ? 12 : 9), actual.Items.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetSummary_CountsWithoutLoadingLayerBreakdownAndLimitsOnlyTheItemQueries(bool includeLegacy)
    {
        var sql = new CapturedSql();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=enseval_sql_capture") // never opened: CapturedSql suppresses it
            .AddInterceptors(sql)
            .Options);

        await new EnsembleService(db, new FakeBinanceKlinesService())
            .GetPredictionEvaluationSummaryAsync("BTCUSDT", 500, includeLegacy);

        var unlimited = sql.Commands.Where(text => !text.Contains("LIMIT")).ToList();
        Assert.NotEmpty(unlimited);
        Assert.All(unlimited, text => Assert.DoesNotContain("LayerBreakdownJson", text));
        var limited = sql.Commands.Where(text => text.Contains("LIMIT")).ToList();
        Assert.Equal(2, limited.Count); // Items and ReevaluatedItems are the only full rows returned
        Assert.All(limited, text => Assert.Contains("LayerBreakdownJson", text));
    }

    [Fact]
    public async Task GetSummary_ClampsReturnedItemsTo200()
    {
        await using var db = CreateDb();
        for (var index = 1; index <= 250; index++)
        {
            db.EnsemblePredictionRecords.Add(Record(index, index, "N"));
            db.EnsemblePredictionRecords.Add(Record(1000 + index, index, "N", r => r.SourcePredictionId = index));
        }
        await db.SaveChangesAsync();
        var service = new EnsembleService(db, new FakeBinanceKlinesService());

        var result = await service.GetPredictionEvaluationSummaryAsync("BTCUSDT", 500);

        Assert.Equal(500, result.TotalPredictions);
        Assert.Equal(200, result.Items.Count);
        Assert.Equal(250, result.ReevaluatedCount);
        Assert.Equal(200, result.ReevaluatedItems.Count);
        Assert.Single((await service.GetPredictionEvaluationSummaryAsync("BTCUSDT", 0)).Items);
    }

    [Fact]
    public async Task LegacyReevaluation_IsImmutableVersionedAndIdempotent()
    {
        await using var db = CreateDb();
        var predictionTime = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeMilliseconds();
        var horizonTime = predictionTime + 24 * 60 * 60 * 1000L;
        var source = new EnsemblePredictionRecord
        {
            Symbol = "BTCUSDT", Timeframe = "1h", TimeMs = predictionTime,
            EntryPrice = 100, FinalDirection = "Bullish", EnsembleConfidence = 0.8,
            ProbUp = 0.7, ProbDown = 0.2, ProbSideways = 0.1,
            EvaluationStatus = "T", ActualPrice24h = 200, ActualReturnPct = 100, EvaluatedAtMs = horizonTime + 1,
            PipelineVersion = ResearchVersions.Legacy, EvaluationVersion = ResearchVersions.Legacy,
            ValidityStatus = ValidityStatuses.Legacy
        };
        db.EnsemblePredictionRecords.Add(source);
        db.Klines.Add(Candle(horizonTime, 90));
        await db.SaveChangesAsync();

        var service = new EnsembleService(db, new FakeBinanceKlinesService());
        await service.EvaluatePredictionsAsync("BTCUSDT", includeLegacy: true);
        var second = await service.EvaluatePredictionsAsync("BTCUSDT", includeLegacy: true);

        await db.Entry(source).ReloadAsync();
        Assert.Equal("T", source.EvaluationStatus);
        Assert.Equal(200, source.ActualPrice24h);
        var child = Assert.Single(await db.EnsemblePredictionRecords.Where(x => x.SourcePredictionId == source.Id).ToListAsync());
        Assert.Equal(ResearchVersions.Evaluation, child.EvaluationVersion);
        Assert.Equal(ValidityStatuses.Legacy, child.ValidityStatus);
        Assert.Equal("F", child.EvaluationStatus);
        Assert.Equal(90, child.ActualPrice24h);
        Assert.Equal(2, await db.EnsemblePredictionRecords.CountAsync());
        Assert.Equal(1, second.TotalPredictions);
        Assert.Equal(1, second.CanonicalEvaluatedCount);
        Assert.Equal(1, second.ReevaluatedCount);
        Assert.Equal(100, second.WinRatePct);
        Assert.Equal(0, second.ReevaluatedWinRatePct);
    }

    [Fact]
    public void Model_EnforcesOneEvaluationVersionPerSourcePrediction()
    {
        using var db = CreateDb();
        var index = db.Model.FindEntityType(typeof(EnsemblePredictionRecord))!.GetIndexes()
            .Single(x => x.Properties.Select(p => p.Name)
                .SequenceEqual(new[] { "SourcePredictionId", "EvaluationVersion" }));
        Assert.True(index.IsUnique);
        Assert.Equal("\"SourcePredictionId\" IS NOT NULL", index.GetFilter());
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private const long BaseMs = 1_700_000_000_000;

    // Structurally valid, versioned and Valid unless edit says otherwise.
    private static EnsemblePredictionRecord Record(long id, int hour, string status, Action<EnsemblePredictionRecord>? edit = null)
    {
        var timeMs = BaseMs + hour * 3_600_000L;
        var evaluated = status is "T" or "F";
        var record = new EnsemblePredictionRecord
        {
            Id = id, Symbol = "BTCUSDT", Timeframe = "1h", TimeMs = timeMs,
            EntryPrice = 100, FinalDirection = "Bullish", ProbUp = 0.6, ProbDown = 0.3, ProbSideways = 0.1,
            EnsembleConfidence = 0.6, LayerBreakdownJson = $"[{{\"layer\":{id}}}]",
            EvaluationStatus = status,
            ActualPrice24h = evaluated ? 101 : null,
            ActualReturnPct = evaluated ? 1 : null,
            EvaluatedAtMs = evaluated ? timeMs + 86_400_000L : null,
            CreatedAtUtc = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc)
        };
        edit?.Invoke(record);
        return record;
    }

    private static void Legacy(EnsemblePredictionRecord record)
    {
        record.PipelineVersion = ResearchVersions.Legacy;
        record.EvaluationVersion = ResearchVersions.Legacy;
        record.ValidityStatus = ValidityStatuses.Legacy;
    }

    private static List<EnsemblePredictionRecord> MixedRecords()
    {
        var archived = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        return new List<EnsemblePredictionRecord>
        {
            // Source-less legacy rows: duplicate keys, structural failures, Invalid, archived, unknown status.
            Record(1, 10, "T", Legacy),
            Record(2, 9, "F", Legacy),
            Record(3, 8, "N", Legacy),
            Record(4, 10, "F", Legacy),
            Record(5, 7, "T", r => { Legacy(r); r.ActualReturnPct = null; }),
            Record(6, 6, "N", r => { Legacy(r); r.ProbUp = double.NaN; }),
            Record(7, 5, "F", r => { Legacy(r); r.ProbUp = 0.5; }),
            Record(8, 4, "T", r => { Legacy(r); r.ValidityStatus = ValidityStatuses.Invalid; }),
            Record(9, 3, "T", r => { Legacy(r); r.ArchivedAtUtc = archived; }),
            Record(10, 2, "X", Legacy),
            Record(11, 11, "F", r => { Legacy(r); r.EntryPrice = double.NaN; }),
            Record(12, 11, "T", Legacy),
            // Versioned rows.
            Record(21, 20, "T"),
            Record(22, 19, "F"),
            Record(23, 18, "N"),
            Record(24, 17, "T", r => r.ArchivedAtUtc = archived),
            Record(25, 20, "N"),
            Record(26, 16, "T", r => r.ActualReturnPct = double.NaN),
            Record(27, 15, "T", r => r.ValidityStatus = ValidityStatuses.Invalid),
            Record(28, 14, "F", r => r.ValidityStatus = ValidityStatuses.Legacy),
            Record(29, 20, "F", r => r.Timeframe = "4h"),
            // Re-evaluations; 34 keeps the legacy evaluation version, so it is not one.
            Record(31, 10, "F", r => { r.SourcePredictionId = 1; r.PipelineVersion = ResearchVersions.Legacy; r.ValidityStatus = ValidityStatuses.Legacy; }),
            Record(32, 9, "T", r => r.SourcePredictionId = 2),
            Record(33, 8, "N", r => { r.SourcePredictionId = 3; r.ArchivedAtUtc = archived; }),
            Record(34, 12, "T", r => { r.SourcePredictionId = 12; r.EvaluationVersion = ResearchVersions.Legacy; }),
            Record(35, 21, "T", r => r.SourcePredictionId = 21),
            // Another symbol never counts.
            Record(41, 30, "T", r => { Legacy(r); r.Symbol = "ETHUSDT"; }),
            Record(42, 30, "T", r => r.Symbol = "ETHUSDT"),
            Record(43, 30, "F", r => { r.Symbol = "ETHUSDT"; r.SourcePredictionId = 41; })
        };
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals // seeded NaN must serialize, not throw
    };

    private static string Json(PredictionEvaluationSummaryDto summary) => JsonSerializer.Serialize(summary, WebJson);

    // EnsembleService.GetPredictionEvaluationSummaryAsync as of backend 1dda5e7 (loads every record, counts in memory),
    // verbatim apart from the context parameter and the symbol policy; the oracle for the rewrite.
    private static async Task<PredictionEvaluationSummaryDto> FullLoadSummaryAsync(
        AppDbContext db, string symbol, int itemLimit, bool includeLegacy, CancellationToken ct = default)
    {
        itemLimit = Math.Clamp(itemLimit, 1, 500);
        var query = db.EnsemblePredictionRecords.AsNoTracking()
            .Where(r => r.Symbol == symbol
                && (includeLegacy || (r.ValidityStatus == ValidityStatuses.Valid && r.ArchivedAtUtc == null)));
        var allRecords = await query.OrderByDescending(r => r.TimeMs).ThenBy(r => r.Id).ToListAsync(ct);
        var records = includeLegacy
            ? allRecords.Where(r => r.SourcePredictionId == null
                && r.PipelineVersion == ResearchVersions.Legacy
                && r.EvaluationVersion == ResearchVersions.Legacy).ToList()
            : allRecords;
        var reevaluated = allRecords.Where(r => r.SourcePredictionId.HasValue && r.EvaluationVersion == ResearchVersions.Evaluation).ToList();
        var total = records.Count;
        var trueCount = records.Count(r => r.EvaluationStatus == "T");
        var falseCount = records.Count(r => r.EvaluationStatus == "F");
        var pendingCount = records.Count(r => r.EvaluationStatus == "N");
        var items = records.Take(itemLimit).ToList();

        var evalCount = trueCount + falseCount;
        var winRate = evalCount > 0 ? (double)trueCount / evalCount * 100.0 : 0.0;
        var canonical = records
            .Where(r => r.ValidityStatus != ValidityStatuses.Invalid)
            .Where(ResearchRecordClassifier.IsStructurallyValid)
            .GroupBy(r => new { r.Symbol, r.Timeframe, r.TimeMs })
            .Select(group => group.OrderBy(r => r.Id).First())
            .ToList();
        var canonicalTrue = canonical.Count(r => r.EvaluationStatus == "T");
        var canonicalFalse = canonical.Count(r => r.EvaluationStatus == "F");
        var canonicalPending = canonical.Count(r => r.EvaluationStatus == "N");
        var canonicalEvaluated = canonicalTrue + canonicalFalse;
        var reevaluatedTrue = reevaluated.Count(r => r.EvaluationStatus == "T");
        var reevaluatedFalse = reevaluated.Count(r => r.EvaluationStatus == "F");
        var reevaluatedPending = reevaluated.Count(r => r.EvaluationStatus == "N");
        var reevaluatedEvaluated = reevaluatedTrue + reevaluatedFalse;

        return new PredictionEvaluationSummaryDto
        {
            Symbol = symbol,
            TotalPredictions = total,
            TrueCount = trueCount,
            FalseCount = falseCount,
            PendingCount = pendingCount,
            WinRatePct = Math.Round(winRate, 2),
            CanonicalEvaluatedCount = canonicalEvaluated,
            CanonicalTrueCount = canonicalTrue,
            CanonicalFalseCount = canonicalFalse,
            CanonicalPendingCount = canonicalPending,
            CanonicalWinRatePct = canonicalEvaluated > 0
                ? Math.Round((double)canonicalTrue / canonicalEvaluated * 100.0, 2)
                : 0,
            ReevaluatedCount = reevaluated.Count,
            ReevaluatedTrueCount = reevaluatedTrue,
            ReevaluatedFalseCount = reevaluatedFalse,
            ReevaluatedPendingCount = reevaluatedPending,
            ReevaluatedWinRatePct = reevaluatedEvaluated > 0
                ? Math.Round((double)reevaluatedTrue / reevaluatedEvaluated * 100.0, 2)
                : 0,
            Items = items,
            ReevaluatedItems = reevaluated.Take(itemLimit).ToList()
        };
    }

    // Records the SQL of every query without a server: the connection is never opened and each reader comes back empty.
    private sealed class CapturedSql : DbCommandInterceptor, IDbConnectionInterceptor
    {
        public List<string> Commands { get; } = new();

        public InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return InterceptionResult<DbDataReader>.SuppressWithResult(new DataTable().CreateDataReader());
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReaderExecuting(command, eventData, result));
    }

    private static Kline Candle(long openTimeMs, decimal open) => new()
    {
        Symbol = "BTCUSDT",
        Timeframe = "1h",
        OpenTimeMs = openTimeMs,
        CloseTimeMs = openTimeMs + 3_599_999,
        Open = open,
        High = open,
        Low = open,
        Close = open,
        Volume = 1
    };
}
