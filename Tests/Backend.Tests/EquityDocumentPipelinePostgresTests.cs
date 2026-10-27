using System.Security.Cryptography;
using Backend.Data;
using Backend.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Backend.Tests;

/// <summary>
/// EQX v3 (worker C) on PostgreSQL: the partial unique index UX_EquityPipelineRuns_OneRunningPerSymbol (and its
/// mapping to PIPELINE_BUSY by the service), and the EquityFinancialFacts core-immutable trigger + provenance CHECK.
/// Runs only with BTC_TEST_POSTGRES (btc_eqx_test, never bitcoin_analyst). Everything happens inside ONE transaction
/// that is rolled back, so no row survives and real pipeline rows other workers write are never changed.
/// </summary>
[Collection(PostgresTestDbCollection.Name)]
public class EquityDocumentPipelinePostgresTests
{
    private static string? ConnectionString() => PostgresTestDb.ConnectionString();

    private static AppDbContext NewDb(string cs) => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options);

    private sealed class NoOpRunner : IEquityPipelineRunner
    {
        public List<EquityPipelineLaunch> Launched { get; } = [];
        public string? Unavailable() => null;
        public Task Launch(EquityPipelineLaunch launch) { Launched.Add(launch); return Task.CompletedTask; }
        public bool TryCancel(Guid runId) => false;
        public bool IsActive(Guid runId) => false;
        public bool IsSymbolActive(string symbol) => false;
        public string Scrub(string text) => text;
        public string? ScrubJson(string? json) => json;
    }

    [Fact]
    public async Task Postgres_OneRunningPerSymbol_UniqueIndex_MapsToPipelineBusy()
    {
        if (ConnectionString() is not { } cs) return;
        await using var db = NewDb(cs);
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            // Another worker may be running a real pipeline on btc_eqx_test: use a symbol without a running row.
            var running = await db.EquityPipelineRuns.AsNoTracking().Where(r => r.Status == "running").Select(r => r.Symbol).ToListAsync();
            var sym = new[] { "BID", "MBB" }.FirstOrDefault(s => !running.Contains(s));
            if (sym is null) return;

            var runner = new NoOpRunner();
            var service = new EquityDocumentPipelineService(db, runner, new EquityDocumentsOptions(), TimeProvider.System, null);
            var first = await service.StartAsync(sym, "incremental", "admin");
            Assert.Equal(EquityDocumentPipelineService.Outcome.Started, first.Outcome);
            // On PostgreSQL there is no pre-check: the second insert hits the partial unique index itself.
            var second = await service.StartAsync(sym, "backfill", "admin");
            Assert.Equal(EquityDocumentPipelineService.Outcome.Busy, second.Outcome);
            Assert.Equal("PIPELINE_BUSY", second.Code);
            Assert.Equal(first.Run!.RunId, second.Run!.RunId);
            Assert.Single(runner.Launched);

            // Raw SQL writers (e.g. the Python CLI) hit the same index; terminal rows are unaffected.
            await tx.CreateSavepointAsync("raw");
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
                """INSERT INTO "EquityPipelineRuns" ("Id","Symbol","Mode","Trigger","Status","StartedAtUtc","HeartbeatAtUtc") VALUES ({0},{1},'backfill','cli','running',now(),now())""",
                Guid.NewGuid(), sym));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
            Assert.Equal(EquityDocumentPipelineService.RunningIndexName, ex.ConstraintName);
            Assert.True(EquityDocumentPipelineService.IsOneRunningViolation(ex));
            await tx.RollbackToSavepointAsync("raw");
            var done = await db.Database.ExecuteSqlRawAsync(
                """INSERT INTO "EquityPipelineRuns" ("Id","Symbol","Mode","Trigger","Status","StartedAtUtc","HeartbeatAtUtc","FinishedAtUtc") VALUES ({0},{1},'backfill','cli','succeeded',now(),now(),now())""",
                Guid.NewGuid(), sym);
            Assert.Equal(1, done);

            // Stale recovery inside the same transaction frees the slot.
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE "EquityPipelineRuns" SET "StartedAtUtc" = now() - interval '2 hours', "HeartbeatAtUtc" = now() - interval '1 hour' WHERE "Id" = {0}""",
                first.Run.RunId);
            var third = await service.StartAsync(sym, "incremental", "worker");
            Assert.Equal(EquityDocumentPipelineService.Outcome.Started, third.Outcome);
            var recovered = await db.EquityPipelineRuns.AsNoTracking().SingleAsync(r => r.Id == first.Run.RunId);
            Assert.Equal("failed", recovered.Status);
            Assert.StartsWith("stale-heartbeat", recovered.Error);
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    [Fact]
    public async Task Postgres_FinancialFacts_CoreImmutable_StatusMutable_ProvenanceChecked()
    {
        if (ConnectionString() is not { } cs) return;
        await using var db = NewDb(cs);
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var at = new DateTime(2002, 5, 2, 0, 0, 0, DateTimeKind.Utc);
            var doc = new EquitySourceDocument
            {
                Id = Guid.NewGuid(), Symbol = "BID", Entity = "test", DocType = "financial-statement", Title = "eqx-v3c pg test",
                SourceUrl = "https://example.test/eqx-v3c.pdf", Provider = "test:eqx-v3c",
                ContentSha256 = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                StoragePath = "BID/eqx-v3c.pdf", ByteSize = 1, MimeType = "application/pdf", ReportingPeriod = "2002Q1",
                Consolidation = "consolidated", PublishedAtUtc = at, FirstSeenAtUtc = at, FetchedAtUtc = at, Revision = 1, CreatedAtUtc = at,
            };
            var extraction = new EquityDocumentExtraction
            {
                Id = Guid.NewGuid(), DocumentId = doc.Id, ExtractorVersion = "eqx-v3c-test", Method = "text-layer", Status = "succeeded",
                PageCount = 2, PagesOcr = 0, StartedAtUtc = at, FinishedAtUtc = at,
            };
            EquityFinancialFact Fact(string key, int? page) => new()
            {
                Id = Guid.NewGuid(), FactKey = key, Symbol = "BID", MetricCode = "TOTAL_ASSETS", MetricDefinitionVersion = "v1",
                MetricGroup = "balance-sheet", Statement = "balance-sheet", PeriodLabel = "2002Q1", PeriodType = "point-in-time",
                PeriodEnd = new DateOnly(2002, 3, 31), FiscalYear = 2002, Consolidation = "consolidated", Value = 123.45m, Unit = "VND",
                Scale = 1_000_000m, RawLabel = "Tổng tài sản", RawValueText = "123,45", RawUnitText = "triệu đồng", ColumnRole = "current",
                PageNumber = page, Location = "p1 r1", DocumentId = doc.Id, ExtractionId = extraction.Id, Status = "extracted",
                AvailableAtUtc = at, CreatedAtUtc = at,
            };
            var fact = Fact($"eqx-v3c-test/{Guid.NewGuid():N}", 1);
            db.EquitySourceDocuments.Add(doc);
            db.EquityDocumentExtractions.Add(extraction);
            db.EquityFinancialFacts.Add(fact);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // Status/ValidationJson may change (EF and raw SQL).
            Assert.Equal(1, await db.Database.ExecuteSqlRawAsync(
                """UPDATE "EquityFinancialFacts" SET "Status" = 'validated', "ValidationJson" = CAST({1} AS jsonb) WHERE "Id" = {0}""", fact.Id, """{"checks":["bs-balance"]}"""));

            // Any core column change is rejected by the trigger, for every writer.
            await tx.CreateSavepointAsync("raw");
            var raw = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
                """UPDATE "EquityFinancialFacts" SET "Value" = 999 WHERE "Id" = {0}""", fact.Id));
            Assert.Equal("23514", raw.SqlState);
            Assert.Contains("immutable", raw.MessageText);
            await tx.RollbackToSavepointAsync("raw");

            await tx.CreateSavepointAsync("ef");
            var tracked = await db.EquityFinancialFacts.SingleAsync(f => f.Id == fact.Id);
            tracked.AvailableAtUtc = at.AddDays(-30); // back-dating availability is exactly what the trigger forbids
            var efEx = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal("23514", Assert.IsType<PostgresException>(efEx.InnerException).SqlState);
            await tx.RollbackToSavepointAsync("ef");
            db.ChangeTracker.Clear();

            // A non-derived fact must point at a real page ⇒ provenance CHECK, for page 0 AND for NULL (the NULL case used to
            // slip through as "PageNumber" > 0 is NULL; closed by migration TightenFinancialFactProvenance).
            foreach (int? badPage in new int?[] { 0, null })
            {
                await tx.CreateSavepointAsync("check");
                var bad = Fact($"eqx-v3c-test/{Guid.NewGuid():N}", 1);
                bad.PageNumber = badPage;
                db.EquityFinancialFacts.Add(bad);
                var check = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                Assert.Equal("CK_EquityFinancialFacts_Provenance", Assert.IsType<PostgresException>(check.InnerException).ConstraintName);
                await tx.RollbackToSavepointAsync("check");
                db.ChangeTracker.Clear();
            }

            var stored = await db.EquityFinancialFacts.AsNoTracking().SingleAsync(f => f.Id == fact.Id);
            Assert.Equal(("validated", 123.45m), (stored.Status, stored.Value));
            var visible = await EquityFinancialFactsService.LoadVisibleAsync(db, "BID", at, null, CancellationToken.None);
            Assert.Contains(visible.Facts, f => f.Id == fact.Id);
            Assert.DoesNotContain((await EquityFinancialFactsService.LoadVisibleAsync(db, "BID", at.AddSeconds(-1), null, CancellationToken.None)).Facts,
                f => f.Id == fact.Id);
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }
}
