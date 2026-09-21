using Backend.Controllers;
using Backend.Data;
using Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Backend.Tests;

public class AlertDeduplicationTests
{
    [Fact]
    public void SourceKeys_AreCanonicalAndEventSpecific()
    {
        Assert.Equal(
            "sequence:default:17:BTCUSDT:1h:123",
            PriceAlertWorker.BuildSequenceSourceKey(" default ", 17, "btcusdt", "1H", 123));
        Assert.Equal(
            "price:default:above:65000:1m:456",
            PriceAlertWorker.BuildPriceSourceKey("default", "ABOVE", 65000.00m, "1M", 456));
    }

    [Fact]
    public async Task AlertWrite_IsIdempotentForSameSourceEvent()
    {
        await using var db = Db();
        var first = await PriceAlertWorker.TryCreateAlertAsync(
            db, null, "default", "sequence_rule", "Rule", "Message", 10, "sequence:default:1:BTCUSDT:1h:1", 30, default);
        var second = await PriceAlertWorker.TryCreateAlertAsync(
            db, null, "default", "sequence_rule", "Rule", "Message", 10, "sequence:default:1:BTCUSDT:1h:1", 30, default);

        Assert.True(first);
        Assert.False(second);
        Assert.Single(await db.AppAlerts.ToListAsync());
    }

    [Fact]
    public async Task Retry_DeliversOneLogicalEventAtMostOnce_WithEvidenceLineage()
    {
        await using var db = Db();
        var telegram = new CountingTelegram();
        const string key = "sequence:default:1:BTCUSDT:4h:100";
        var first = await PriceAlertWorker.TryCreateAlertAsync(db, telegram, "default", "sequence_rule",
            "Rule", "Message", 10, key, 30, default, "observed-event", "rules-v2:rule:1", 200);
        var retry = await PriceAlertWorker.TryCreateAlertAsync(db, telegram, "default", "sequence_rule",
            "Rule", "Message", 10, key, 30, default, "observed-event", "rules-v2:rule:1", 200);

        Assert.True(first);
        Assert.False(retry);
        Assert.Equal(1, telegram.SendCount);
        var alert = Assert.Single(await db.AppAlerts.ToListAsync());
        Assert.Equal("observed-event", alert.EvidenceKind);
        Assert.Equal("rules-v2:rule:1", alert.Provenance);
        Assert.Equal(200, alert.AvailableTimeMs);
        Assert.Equal("delivered", alert.DeliveryStatus);
    }

    [Fact]
    public async Task ProviderFalse_IsNotRecordedAsDelivered_AndIsNotRetried()
    {
        await using var db = Db();
        var telegram = new CountingTelegram(deliveryResult: false);
        const string key = "price:default:above:70000:4h:100";
        await PriceAlertWorker.TryCreateAlertAsync(db, telegram, "default", "price_above",
            "Threshold", "Message", 70_001, key, 30, default);
        await PriceAlertWorker.TryCreateAlertAsync(db, telegram, "default", "price_above",
            "Threshold", "Message", 70_001, key, 30, default);

        var alert = Assert.Single(await db.AppAlerts.ToListAsync());
        Assert.Equal(1, telegram.SendCount);
        Assert.Equal("failed-at-most-once", alert.DeliveryStatus);
        Assert.Null(alert.DeliveredAtUtc);
        Assert.Contains("returned false", alert.DeliveryError);
    }

    [Fact]
    public async Task Deduplicate_NeverGuessesForNullKeys_AndArchivesExactKeyDuplicates()
    {
        await using var db = Db();
        db.AppAlerts.AddRange(
            Alert(null, 1),
            Alert(null, 2),
            Alert("event", 3),
            Alert("event", 4));
        await db.SaveChangesAsync();
        var controller = new AlertsController(db);

        var result = Assert.IsType<OkObjectResult>((await controller.Deduplicate(apply: true)).Result);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"duplicateAlertCount\":1", json);
        Assert.Equal(3, await db.AppAlerts.CountAsync(x => x.ArchivedAtUtc == null));
        Assert.Equal(2, await db.AppAlerts.CountAsync(x => x.SourceKey == null && x.ArchivedAtUtc == null));
    }

    [Fact]
    public void Model_HasFilteredUniqueSourceKeyIndex()
    {
        using var db = Db();
        var index = db.Model.FindEntityType(typeof(AppAlert))!.GetIndexes()
            .Single(x => x.Properties.Select(p => p.Name).SequenceEqual(new[] { "UserId", "SourceKey" }));
        Assert.True(index.IsUnique);
        Assert.Equal("\"SourceKey\" IS NOT NULL AND \"ArchivedAtUtc\" IS NULL", index.GetFilter());
    }

    [Fact]
    public void Migration_ArchivesDuplicatesDeterministicallyBeforeCreatingUniqueIndex()
    {
        var operations = new ExposedMigration().Operations();
        var archiveSql = operations.OfType<SqlOperation>()
            .Single(operation => operation.Sql.Contains("duplicate_rank", StringComparison.Ordinal));
        var index = operations.OfType<CreateIndexOperation>()
            .Single(operation => operation.Name == "IX_AppAlerts_UserId_SourceKey");

        Assert.Contains("ORDER BY \"CreatedAt\", \"Id\"", archiveSql.Sql);
        Assert.Contains("r.duplicate_rank > 1", archiveSql.Sql);
        Assert.Equal("\"SourceKey\" IS NOT NULL AND \"ArchivedAtUtc\" IS NULL", index.Filter);
    }

    private static AppAlert Alert(string? key, int seconds) => new()
    {
        Id = Guid.NewGuid(), UserId = "default", Type = "sequence_rule", Title = "same",
        Message = "same", PriceSnapshot = 10, CreatedAt = DateTimeOffset.UtcNow.AddSeconds(seconds),
        SourceKey = key
    };

    private static AppDbContext Db()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private sealed class ExposedMigration : Backend.Migrations.AddResearchValidityAndAlertDeduplication
    {
        public IReadOnlyList<MigrationOperation> Operations()
        {
            var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
            Up(builder);
            return builder.Operations;
        }
    }

    private sealed class CountingTelegram : ITelegramNotificationService
    {
        private readonly bool _deliveryResult;
        public CountingTelegram(bool deliveryResult = true) => _deliveryResult = deliveryResult;
        public int SendCount { get; private set; }
        public Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.FromResult(_deliveryResult);
        }

        public Task<bool> SendTradeExecutionAlertAsync(Backend.Services.Models.TradeExecutionAlertDto alert, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public string FormatTradeExecutionMessage(Backend.Services.Models.TradeExecutionAlertDto alert) => string.Empty;
    }
}
