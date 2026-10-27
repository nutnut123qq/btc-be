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
    public void BacktestClassifier_FiniteLedgerKeepsItsClassification()
    {
        Assert.Equal((ValidityStatuses.Valid, (string?)null), ResearchRecordClassifier.Classify(FiniteRunWithLedger()));

        var legacy = FiniteRunWithLedger();
        legacy.EvaluationVersion = ResearchVersions.Legacy;
        Assert.Equal(ValidityStatuses.Legacy, ResearchRecordClassifier.Classify(legacy).Status);
    }

    [Theory]
    [MemberData(nameof(NonFiniteBacktestValues))]
    public void BacktestClassifier_RejectsAnyNonFiniteValueBeforeLedgerChecks(string field, double value)
    {
        var run = FiniteRunWithLedger();
        BacktestFieldSetters[field](run, value);

        Assert.Equal(
            (ValidityStatuses.Invalid, (string?)"Backtest contains a non-finite accounting value."),
            ResearchRecordClassifier.Classify(run));
    }

    [Theory]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(101.0, double.NaN)]
    [InlineData(101.0, double.PositiveInfinity)]
    [InlineData(101.0, double.NegativeInfinity)]
    public void EnsembleClassifier_RejectsNonFiniteEvaluation(double actualPrice, double actualReturnPct)
    {
        var finite = EvaluatedEnsemble(101.0, 1.0);
        Assert.Equal(ValidityStatuses.Valid, ResearchRecordClassifier.Classify(finite, false).Status);
        Assert.True(ResearchRecordClassifier.IsStructurallyValid(finite));

        var record = EvaluatedEnsemble(actualPrice, actualReturnPct);
        Assert.Contains("EVALUATION_FIELDS", ResearchRecordClassifier.Classify(record, false).Reason);
        Assert.False(ResearchRecordClassifier.IsStructurallyValid(record));
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

    private static readonly Dictionary<string, Action<BacktestRun, double>> BacktestFieldSetters = new()
    {
        ["TotalReturnPct"] = (run, value) => run.TotalReturnPct = value,
        ["FinalEquity"] = (run, value) => run.FinalEquity = value,
        ["WinRate"] = (run, value) => run.WinRate = value,
        ["BuyHoldReturnPct"] = (run, value) => run.BuyHoldReturnPct = value,
        ["MaxDrawdownPct"] = (run, value) => run.MaxDrawdownPct = value,
        ["SharpeRatio"] = (run, value) => run.SharpeRatio = value,
        ["SortinoRatio"] = (run, value) => run.SortinoRatio = value,
        ["ProfitFactor"] = (run, value) => run.ProfitFactor = value,
        ["FeeBps"] = (run, value) => run.FeeBps = value,
        ["SlippageBps"] = (run, value) => run.SlippageBps = value,
        ["Trade.GrossReturn"] = (run, value) => run.Trades[2].GrossReturn = value,
        ["Trade.NetReturn"] = (run, value) => run.Trades[2].NetReturn = value,
        ["Trade.PnlPct"] = (run, value) => run.Trades[2].PnlPct = value,
        ["Trade.Confidence"] = (run, value) => run.Trades[2].Confidence = value,
        ["Trade.TargetReturn"] = (run, value) => run.Trades[2].TargetReturn = value,
    };

    public static TheoryData<string, double> NonFiniteBacktestValues()
    {
        var data = new TheoryData<string, double>();
        foreach (var field in BacktestFieldSetters.Keys)
            foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                data.Add(field, value);
        return data;
    }

    // Five trades so the "fewer than five trades" plausibility branch never decides the outcome.
    private static BacktestRun FiniteRunWithLedger()
    {
        var run = Run();
        for (var i = 0; i < 5; i++)
        {
            run.Trades.Add(new BacktestTrade
            {
                GrossReturn = 0.012, NetReturn = 0.01, PnlPct = 1.0, Confidence = 0.7,
                TargetReturn = i % 2 == 0 ? 0.01 : null
            });
        }
        run.TotalTrades = 5;
        run.TotalReturnPct = (Math.Pow(1.01, 5) - 1) * 100;
        run.FinalEquity = 10_000 * Math.Pow(1.01, 5);
        run.WinRate = 1;
        run.BuyHoldReturnPct = 3;
        run.MaxDrawdownPct = 0.5;
        run.SharpeRatio = 2;
        run.SortinoRatio = 2.5;
        run.ProfitFactor = 99.9;
        run.FeeBps = 10;
        run.SlippageBps = 5;
        return run;
    }

    private static EnsemblePredictionRecord EvaluatedEnsemble(double actualPrice, double actualReturnPct) => new()
    {
        EntryPrice = 100, FinalDirection = "Bullish", EvaluationStatus = "T",
        ActualPrice24h = actualPrice, ActualReturnPct = actualReturnPct, EvaluatedAtMs = 1,
        ProbDown = 0.2, ProbSideways = 0.3, ProbUp = 0.5
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
