using Backend.Controllers;
using Backend.Data;
using Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backend.Tests;

public class ResearchValidityTests
{
    [Fact]
    public void BacktestClassifier_SeparatesInvalidLegacyAndValid()
    {
        var invalid = Run();
        invalid.TotalTrades = 1;
        invalid.TotalReturnPct = 10;
        invalid.Trades.Add(new BacktestTrade { NetReturn = 0, PnlPct = 10 });
        Assert.Equal(ValidityStatuses.Invalid, ResearchRecordClassifier.Classify(invalid).Status);

        var legacy = Run();
        legacy.PipelineVersion = ResearchVersions.Legacy;
        Assert.Equal(ValidityStatuses.Legacy, ResearchRecordClassifier.Classify(legacy).Status);

        Assert.Equal(ValidityStatuses.Valid, ResearchRecordClassifier.Classify(Run()).Status);
    }

    [Fact]
    public void PredictionAndEnsembleClassifier_RejectBadStructureAndDuplicateEvents()
    {
        var prediction = new ModelPrediction
        {
            Symbol = "BTCUSDT", Timeframe = "1h", Horizon = "1h", WindowSize = 5,
            WindowEndMs = 1, ModelVersion = "model", PredictedLabel = 1,
            ProbDown = 0.2, ProbSideways = 0.3, ProbUp = 0.5
        };
        Assert.Equal(ValidityStatuses.Invalid, ResearchRecordClassifier.Classify(prediction, true).Status);
        prediction.ProbUp = 0.8;
        Assert.Contains("PROBABILITIES_NOT_NORMALIZED", ResearchRecordClassifier.Classify(prediction, false).Reason);

        var ensemble = new EnsemblePredictionRecord
        {
            EntryPrice = 1, FinalDirection = "Bullish", EvaluationStatus = "N",
            ProbDown = 0.2, ProbSideways = 0.3, ProbUp = 0.5
        };
        Assert.Equal(ValidityStatuses.Invalid, ResearchRecordClassifier.Classify(ensemble, true).Status);
        ensemble.EvaluationStatus = "T";
        Assert.Contains("EVALUATION_FIELDS", ResearchRecordClassifier.Classify(ensemble, false).Reason);
    }

    [Fact]
    public async Task BacktestApi_HidesLegacyByDefault_AndLabCanIncludeIt()
    {
        await using var db = Db();
        db.BacktestRuns.AddRange(
            Run(),
            new BacktestRun
            {
                Symbol = "BTCUSDT", Timeframe = "1h", ModelName = "old",
                PipelineVersion = ResearchVersions.Legacy, EvaluationVersion = ResearchVersions.Legacy,
                ValidityStatus = ValidityStatuses.Legacy, CreatedAtUtc = DateTime.UtcNow
            });
        await db.SaveChangesAsync();
        var controller = new BacktestController(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<BacktestController>.Instance);

        var core = Assert.IsType<OkObjectResult>((await controller.GetRuns()).Result);
        var lab = Assert.IsType<OkObjectResult>((await controller.GetRuns(includeLegacy: true)).Result);
        Assert.Contains("\"count\":1", System.Text.Json.JsonSerializer.Serialize(core.Value));
        Assert.Contains("\"count\":2", System.Text.Json.JsonSerializer.Serialize(lab.Value));
    }

    [Fact]
    public async Task ClassificationApply_IsIdempotentAndDoesNotReconsiderInvalidDuplicate()
    {
        await using var db = Db();
        db.ModelPredictions.AddRange(Prediction(), Prediction());
        await db.SaveChangesAsync();
        var controller = new BacktestController(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<BacktestController>.Instance);

        await controller.ClassifyRecords(apply: true);
        var invalid = await db.ModelPredictions.SingleAsync(x => x.ValidityStatus == ValidityStatuses.Invalid);
        var archivedAt = invalid.ArchivedAtUtc;
        await controller.ClassifyRecords(apply: true);

        Assert.Single(await db.ModelPredictions.Where(x => x.ValidityStatus == ValidityStatuses.Invalid).ToListAsync());
        Assert.Equal(archivedAt, invalid.ArchivedAtUtc);
    }

    [Fact]
    public async Task ClassificationDryRun_HasStablePrimaryKeyOrder()
    {
        await using var db = Db();
        var p30 = Prediction(); p30.Id = 30; p30.WindowEndMs = 30;
        var p10 = Prediction(); p10.Id = 10; p10.WindowEndMs = 10;
        var p20 = Prediction(); p20.Id = 20; p20.WindowEndMs = 20;
        db.ModelPredictions.AddRange(p30, p10, p20);
        await db.SaveChangesAsync();
        var controller = new BacktestController(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<BacktestController>.Instance);
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

        var first = Assert.IsType<OkObjectResult>((await controller.ClassifyRecords()).Result);
        var second = Assert.IsType<OkObjectResult>((await controller.ClassifyRecords()).Result);
        var firstJson = System.Text.Json.JsonSerializer.Serialize(first.Value, options);
        var secondJson = System.Text.Json.JsonSerializer.Serialize(second.Value, options);

        Assert.Equal(firstJson, secondJson);
        Assert.Contains("\"id\":10", firstJson);
        Assert.Contains("\"id\":20", firstJson);
        Assert.Contains("\"id\":30", firstJson);
        Assert.True(firstJson.IndexOf("\"id\":10", StringComparison.Ordinal) < firstJson.IndexOf("\"id\":20", StringComparison.Ordinal));
        Assert.True(firstJson.IndexOf("\"id\":20", StringComparison.Ordinal) < firstJson.IndexOf("\"id\":30", StringComparison.Ordinal));
    }

    [Fact]
    public void BatchReplay_IsFailClosedByDefault()
    {
        var result = new BatchReplayResultDto();
        Assert.Equal(ValidityStatuses.Invalid, result.ValidityStatus);
        Assert.False(result.Validated);
        Assert.False(string.IsNullOrWhiteSpace(result.InvalidReason));
    }

    private static BacktestRun Run() => new()
    {
        Symbol = "BTCUSDT",
        Timeframe = "1h",
        ModelName = "model",
        PipelineVersion = ResearchVersions.DataPipeline,
        EvaluationVersion = ResearchVersions.Evaluation,
        ValidityStatus = ValidityStatuses.Valid,
        CreatedAtUtc = DateTime.UtcNow
    };

    private static ModelPrediction Prediction() => new()
    {
        Symbol = "BTCUSDT", Timeframe = "1h", WindowSize = 5, Horizon = "1h", WindowEndMs = 1,
        ModelVersion = "model", PredictedLabel = 1, ProbUp = 0.7, ProbDown = 0.2, ProbSideways = 0.1
    };

    private static AppDbContext Db()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
