using Backend.Controllers;
using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Tests;

public class ProductionTimeframeWriteBoundaryTests
{
    [Fact]
    public async Task EnsemblePredict_RejectsInactiveTimeframeWithoutCallingService()
    {
        var service = new RecordingEnsembleService();
        var controller = WithHttpContext(new EnsembleController(service));

        var result = await controller.PredictEnsemble(timeframe: "15m");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ApiErrorEnvelope>(badRequest.Value);
        Assert.Equal("INACTIVE_TIMEFRAME", error.Code);
        Assert.False(service.PredictCalled);
    }

    [Fact]
    public async Task EnsemblePredict_CanonicalizesActiveTimeframeBeforeCallingService()
    {
        var service = new RecordingEnsembleService();
        var controller = WithHttpContext(new EnsembleController(service));

        var result = await controller.PredictEnsemble(timeframe: " 4H ");

        Assert.IsType<OkObjectResult>(result);
        Assert.True(service.PredictCalled);
        Assert.Equal("4h", service.LastTimeframe);
    }

    [Fact]
    public async Task VolumeProfileGet_RejectsInactiveTimeframeEvenThoughRouteIsGet()
    {
        var service = new RecordingVolumeProfileService();
        var controller = WithHttpContext(new VolumeProfileController(service));

        var result = await controller.GetCurrent(timeframe: "1m");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ApiErrorEnvelope>(badRequest.Value);
        Assert.Equal("INACTIVE_TIMEFRAME", error.Code);
        Assert.False(service.Called);
    }

    private static T WithHttpContext<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private sealed class RecordingVolumeProfileService : IVolumeProfileService
    {
        public bool Called { get; private set; }

        public Task<VolumeProfileSnapshot?> GetVolumeProfileAsync(
            string symbol,
            string timeframe,
            int lookbackBars,
            CancellationToken ct = default)
        {
            Called = true;
            return Task.FromResult<VolumeProfileSnapshot?>(null);
        }
    }

    private sealed class RecordingEnsembleService : IEnsembleService
    {
        public bool PredictCalled { get; private set; }
        public string? LastTimeframe { get; private set; }

        public Task<EnsemblePredictionRecord> PredictEnsembleAsync(string symbol, string timeframe, CancellationToken ct = default)
        {
            PredictCalled = true;
            LastTimeframe = timeframe;
            return Task.FromResult(new EnsemblePredictionRecord
            {
                Symbol = symbol,
                Timeframe = timeframe,
                FinalDirection = "Sideways"
            });
        }

        public Task<List<EnsemblePredictionRecord>> GetEnsembleHistoryAsync(string symbol, string timeframe, int limit, bool includeLegacy = false, CancellationToken ct = default) =>
            Task.FromResult(new List<EnsemblePredictionRecord>());

        public Task<PredictionEvaluationSummaryDto> EvaluatePredictionsAsync(string symbol = "BTCUSDT", int itemLimit = 100, bool includeLegacy = false, CancellationToken ct = default) =>
            Task.FromResult(new PredictionEvaluationSummaryDto());

        public Task<PredictionEvaluationSummaryDto> GetPredictionEvaluationSummaryAsync(string symbol = "BTCUSDT", int itemLimit = 100, bool includeLegacy = false, CancellationToken ct = default) =>
            Task.FromResult(new PredictionEvaluationSummaryDto());

        public Task<BatchReplayResultDto> BatchReplayAsync(
            int sampleCount = 2000,
            double minConfidence = 0.60,
            bool enableMtfFilter = true,
            bool enableSmcFilter = true,
            bool enableAtrRrEngine = true,
            bool enableVolumeFilter = true,
            bool enableMlClassifier = true,
            bool enableKellySizing = true,
            string symbol = "BTCUSDT",
            string timeframe = "4h",
            CancellationToken ct = default) => Task.FromResult(new BatchReplayResultDto());
    }
}
