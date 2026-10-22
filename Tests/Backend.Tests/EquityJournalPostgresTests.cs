using Backend.Data;
using Backend.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.Tests;

/// <summary>
/// Postgres test classes that seed/clean the shared 2002-2004 window of the throwaway test DB must not
/// run in parallel with each other (one class's cleanup would delete another's fixture rows).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresTestDbCollection
{
    public const string Name = "PostgresTestDb";
}

/// <summary>
/// EQX v2 A2 — journal constraints on PostgreSQL (migration EquityResearchSessionLane): NULL-safe
/// fill/outcome coherence CHECKs on MbbSignalObservations, and the write-once / immutable triggers on
/// MbbSignalObservations, EquityForecastRecords and EquityResearchNotes, exercised through both EF and raw
/// SQL so every writer is covered. Runs only with BTC_TEST_POSTGRES (btc_eqx_test, never bitcoin_analyst);
/// rows live in 2002-2003 and are removed afterwards.
/// </summary>
[Collection(PostgresTestDbCollection.Name)]
public class EquityJournalPostgresTests
{
    private static readonly long WindowEndMs = new DateTimeOffset(2005, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private const string NotePrefix = "session/BID/v2a-pg-test/";

    private static string? ConnectionString() => Environment.GetEnvironmentVariable("BTC_TEST_POSTGRES");

    private static AppDbContext NewDb(string cs) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options);

    private static async Task CleanupAsync(string cs)
    {
        await using var db = NewDb(cs);
        string[] symbols = ["MBB", "BID"];
        await db.MbbSignalObservations
            .Where(o => symbols.Contains(o.Symbol) && o.SignalBarOpenTimeMs < WindowEndMs).ExecuteDeleteAsync();
        await db.EquityForecastRecords.Where(f => f.SignalBarOpenTimeMs < WindowEndMs).ExecuteDeleteAsync();
        await db.EquityResearchNotes.Where(n => n.NoteId.StartsWith(NotePrefix)).ExecuteDeleteAsync();
    }

    private static string AllMessages(Exception ex)
    {
        var parts = new List<string>();
        for (var e = (Exception?)ex; e is not null; e = e.InnerException) parts.Add(e.Message);
        return string.Join(" | ", parts);
    }

    private static async Task AssertRejectedAsync(Func<Task> act, string fragment)
    {
        var ex = await Record.ExceptionAsync(act);
        Assert.NotNull(ex);
        Assert.Contains(fragment, AllMessages(ex!));
    }

    private static readonly DateTimeOffset At = new(2003, 3, 12, 9, 0, 0, TimeSpan.Zero); // 16:00 local

    private static MbbSignalObservation Obs(long signal, Action<MbbSignalObservation>? edit = null)
    {
        var row = new MbbSignalObservation
        {
            Id = Guid.NewGuid(),
            DecisionId = MbbSignalObservationService.DecisionIdFor("BID", "1d", signal),
            RecorderVersion = MbbSignalObservationService.RecorderVersion,
            Symbol = "BID", Timeframe = "1d",
            SignalBarOpenTimeMs = signal, SignalBarCloseTimeMs = signal + 27_900_000L,
            ObservedAtUtc = At.UtcDateTime, AvailableTimeMs = At.ToUnixTimeMilliseconds(),
            Decision = "sell", PriceBasis = "adjusted", DecisionPayloadJson = "{}", CreatedAtUtc = At.UtcDateTime,
            MarketCloseVnd = 30_000m,
        };
        edit?.Invoke(row);
        return row;
    }

    private static void SetFill(MbbSignalObservation r)
    {
        r.FillPrice = 30_100m;
        r.FillObservedAtUtc = At.UtcDateTime.AddDays(1);
        r.FillSource = "next-session-open";
        r.FillBarOpenTimeMs = At.ToUnixTimeMilliseconds() + 86_400_000L;
    }

    private static void SetOutcome(MbbSignalObservation r)
    {
        r.OutcomeReturn = 0.012;
        r.OutcomeObservedAtUtc = At.UtcDateTime.AddDays(2);
        r.OutcomeHorizon = "1-session";
    }

    private static long Signal(int day) => VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2003, 3, day));

    [Fact]
    public async Task Observations_FillOutcomeCoherence_AllNullOk_AllSetOk_PartialRejected()
    {
        var cs = ConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return;
        try
        {
            await using (var ok = NewDb(cs))
            {
                ok.MbbSignalObservations.Add(Obs(Signal(3)));                                   // all null
                ok.MbbSignalObservations.Add(Obs(Signal(4), r => { SetFill(r); SetOutcome(r); })); // all set
                ok.MbbSignalObservations.Add(Obs(Signal(5), SetFill));                          // fill only
                await ok.SaveChangesAsync();
            }

            var partial = new (string Constraint, Action<MbbSignalObservation> Edit)[]
            {
                ("CK_MbbSignalObservations_FillCoherence", r => { SetFill(r); r.FillObservedAtUtc = null; }),
                // These two passed the old CHECK (NULL comparison = unknown = pass).
                ("CK_MbbSignalObservations_FillCoherence", r => { SetFill(r); r.FillBarOpenTimeMs = null; }),
                ("CK_MbbSignalObservations_FillCoherence", r => { SetFill(r); r.FillSource = null; }),
                ("CK_MbbSignalObservations_FillCoherence", r => { r.FillPrice = 30_100m; }),
                ("CK_MbbSignalObservations_OutcomeCoherence", r => { SetFill(r); SetOutcome(r); r.OutcomeObservedAtUtc = null; }),
                ("CK_MbbSignalObservations_OutcomeCoherence", r => { SetFill(r); SetOutcome(r); r.OutcomeHorizon = null; }),
                ("CK_MbbSignalObservations_OutcomeCoherence", r => { SetOutcome(r); }), // outcome without fill
                ("CK_MbbSignalObservations_FillCoherence", r => { SetFill(r); r.Decision = "wait"; }),
            };
            // Signal bars must close before the 2003-03-12 observation moment (chronology CHECK).
            var days = new Queue<int>([1, 2, 6, 7, 8, 9, 10, 11]);
            foreach (var (constraint, edit) in partial)
            {
                await using var bad = NewDb(cs);
                bad.MbbSignalObservations.Add(Obs(Signal(days.Dequeue()), edit));
                await AssertRejectedAsync(() => bad.SaveChangesAsync(), constraint);
            }
        }
        finally { await CleanupAsync(cs); }
    }

    [Fact]
    public async Task Observations_Trigger_ImmutableCore_WriteOnceFillOutcome_EfAndRawSql()
    {
        var cs = ConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return;
        try
        {
            var row = Obs(Signal(3));
            await using (var db = NewDb(cs)) { db.MbbSignalObservations.Add(row); await db.SaveChangesAsync(); }

            // Immutable decision core: EF and raw SQL.
            await using (var db = NewDb(cs))
            {
                var r = await db.MbbSignalObservations.SingleAsync(o => o.Id == row.Id);
                r.Decision = "hold";
                await AssertRejectedAsync(() => db.SaveChangesAsync(), "immutable columns of MbbSignalObservations");
            }
            await using (var db = NewDb(cs))
                await AssertRejectedAsync(() => db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"MbbSignalObservations\" SET \"MarketCloseVnd\" = 1 WHERE \"Id\" = {row.Id}"),
                    "immutable columns of MbbSignalObservations");
            await using (var db = NewDb(cs))
                await AssertRejectedAsync(() => db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"MbbSignalObservations\" SET \"DecisionPayloadJson\" = '{{\"x\":1}}' WHERE \"Id\" = {row.Id}"),
                    "immutable columns of MbbSignalObservations");

            // NULL → value is allowed (the resolver path), then the value is frozen.
            await using (var db = NewDb(cs))
            {
                var r = await db.MbbSignalObservations.SingleAsync(o => o.Id == row.Id);
                SetFill(r);
                await db.SaveChangesAsync();
                SetOutcome(r);
                await db.SaveChangesAsync();
                // Re-saving identical values is a no-op for the trigger.
                db.Entry(r).State = EntityState.Modified;
                await db.SaveChangesAsync();
            }
            await using (var db = NewDb(cs))
            {
                var r = await db.MbbSignalObservations.SingleAsync(o => o.Id == row.Id);
                r.FillPrice = 31_000m;
                await AssertRejectedAsync(() => db.SaveChangesAsync(), "write-once column FillPrice already set");
            }
            await using (var db = NewDb(cs))
                await AssertRejectedAsync(() => db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"MbbSignalObservations\" SET \"OutcomeReturn\" = 0.5 WHERE \"Id\" = {row.Id}"),
                    "write-once column OutcomeReturn already set");
            await using (var db = NewDb(cs))
                await AssertRejectedAsync(() => db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"MbbSignalObservations\" SET \"OutcomeReturn\" = NULL, \"OutcomeObservedAtUtc\" = NULL, \"OutcomeHorizon\" = NULL WHERE \"Id\" = {row.Id}"),
                    "write-once column OutcomeReturn already set"); // clearing is a change too

            await using (var check = NewDb(cs))
            {
                var r = await check.MbbSignalObservations.AsNoTracking().SingleAsync(o => o.Id == row.Id);
                Assert.Equal((30_100m, 0.012, "sell"), (r.FillPrice!.Value, r.OutcomeReturn!.Value, r.Decision));
            }
        }
        finally { await CleanupAsync(cs); }
    }

    private static EquityForecastRecord Forecast(long signal) => new()
    {
        Id = Guid.NewGuid(),
        ForecastId = $"quant-knn/BID/tb-p5-s4-h20/{signal}/{Guid.NewGuid():N}",
        Source = "quant-knn", Symbol = "BID", Timeframe = "1d", ConfigId = "tb-p5-s4-h20",
        TargetPct = 0.05, StopPct = 0.04, HorizonSessions = 20,
        SignalBarOpenTimeMs = signal, SignalBarCloseTimeMs = signal + 27_900_000, AvailableTimeMs = signal + 30_000_000,
        CreatedAtUtc = DateTime.UtcNow, ProbTargetFirst = 0.4, ProbStopFirst = 0.5, Abstained = false,
        Validated = false, MethodVersion = "equity-knn-v1", InputSnapshotSha256 = new string('0', 64),
    };

    [Fact]
    public async Task ForecastRecords_Trigger_ImmutableForecast_WriteOnceOutcome_EfAndRawSql()
    {
        var cs = ConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return;
        try
        {
            var row = Forecast(Signal(12));
            await using (var db = NewDb(cs)) { db.EquityForecastRecords.Add(row); await db.SaveChangesAsync(); }

            await using (var db = NewDb(cs))
            {
                var r = await db.EquityForecastRecords.SingleAsync(f => f.Id == row.Id);
                r.ProbTargetFirst = 0.45;
                await AssertRejectedAsync(() => db.SaveChangesAsync(), "immutable columns of EquityForecastRecords");
            }
            await using (var db = NewDb(cs))
                await AssertRejectedAsync(() => db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"EquityForecastRecords\" SET \"Validated\" = TRUE WHERE \"Id\" = {row.Id}"),
                    "immutable columns of EquityForecastRecords");

            // Outcome NULL → value (resolver path) is allowed once.
            await using (var db = NewDb(cs))
            {
                var r = await db.EquityForecastRecords.SingleAsync(f => f.Id == row.Id);
                r.EntryBarOpenTimeMs = r.SignalBarOpenTimeMs + 86_400_000L;
                r.EntryPrice = 30_000m;
                r.OutcomeLabel = "target-first";
                r.OutcomeReturnNet = 0.046;
                r.ResolvedBarOpenTimeMs = r.SignalBarOpenTimeMs + 3 * 86_400_000L;
                r.OutcomeResolvedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            await using (var db = NewDb(cs))
            {
                var r = await db.EquityForecastRecords.SingleAsync(f => f.Id == row.Id);
                r.OutcomeLabel = "stop-first";
                await AssertRejectedAsync(() => db.SaveChangesAsync(), "write-once column OutcomeLabel already set");
            }
            await using (var db = NewDb(cs))
                await AssertRejectedAsync(() => db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"EquityForecastRecords\" SET \"EntryPrice\" = NULL, \"OutcomeLabel\" = NULL, \"OutcomeReturnNet\" = NULL, \"OutcomeResolvedAtUtc\" = NULL, \"EntryBarOpenTimeMs\" = NULL, \"ResolvedBarOpenTimeMs\" = NULL WHERE \"Id\" = {row.Id}"),
                    "write-once column");
            await using (var check = NewDb(cs))
                Assert.Equal("target-first",
                    (await check.EquityForecastRecords.AsNoTracking().SingleAsync(f => f.Id == row.Id)).OutcomeLabel);
        }
        finally { await CleanupAsync(cs); }
    }

    [Fact]
    public async Task ResearchNotes_AreImmutable_EfAndRawSql()
    {
        var cs = ConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return;
        var signal = Signal(12);
        var note = new EquityResearchNote
        {
            Id = Guid.NewGuid(),
            NoteId = NotePrefix + Guid.NewGuid().ToString("N")[..8],
            Symbol = "BID", Mode = "retrospective",
            RequestedAsOfUtc = new DateTime(2003, 3, 12, 9, 0, 0, DateTimeKind.Utc),
            SignalBarOpenTimeMs = signal, SignalBarCloseTimeMs = signal + 27_900_000L, HorizonSessions = 20,
            SnapshotId = "snap/BID/1047427200000/abcdefabcdef", SnapshotSha256 = new string('a', 64),
            SkillVersion = "investment-research-test", MethodVersion = "test",
            Direction = "no-view", ActionView = "none", ActionBasis = "market-only",
            BodyJson = "{\"schemaVersion\":\"equity-research-note-v1\",\"thesis\":\"t\"}",
            AvailableTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), CreatedAtUtc = DateTime.UtcNow,
        };
        try
        {
            await using (var db = NewDb(cs)) { db.EquityResearchNotes.Add(note); await db.SaveChangesAsync(); }
            await using (var db = NewDb(cs))
            {
                var r = await db.EquityResearchNotes.SingleAsync(n => n.Id == note.Id);
                r.Direction = "up";
                await AssertRejectedAsync(() => db.SaveChangesAsync(), "EquityResearchNotes rows are immutable");
            }
            await using (var db = NewDb(cs))
                await AssertRejectedAsync(() => db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"EquityResearchNotes\" SET \"BodyJson\" = '{{}}'::jsonb WHERE \"Id\" = {note.Id}"),
                    "EquityResearchNotes rows are immutable");
            await using (var check = NewDb(cs))
                Assert.Equal("no-view", (await check.EquityResearchNotes.AsNoTracking().SingleAsync(n => n.Id == note.Id)).Direction);
        }
        finally { await CleanupAsync(cs); }
    }
}
