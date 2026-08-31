using System.Text.Json;
using Backend.Data;
using Backend.Controllers;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class TransitionServiceContractTests
{
    [Fact]
    public void BuildEndpoint_ReportsUnavailableInsteadOfFalseTrigger()
    {
        var controller = new TransitionsController(null!)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
            }
        };

        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(controller.BuildMatrix());
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status501NotImplemented, response.StatusCode);
        Assert.Equal("TRANSITION_BUILD_UNAVAILABLE", Assert.IsType<ApiErrorEnvelope>(response.Value).Code);
    }

    [Fact]
    public async Task MatrixAndFrom_ReturnFrontendContractWithArchetypeCodes()
    {
        await using var db = CreateDb();
        var from = new CandleArchetype { Id = 1, ArchetypeCode = "A", Symbol = "BTCUSDT", Timeframe = "1h", WindowSize = 20 };
        var to = new CandleArchetype { Id = 2, ArchetypeCode = "B", Symbol = "BTCUSDT", Timeframe = "1h", WindowSize = 20 };
        db.CandleArchetypes.AddRange(from, to);
        db.ArchetypeTransitions.Add(new ArchetypeTransition
        {
            Id = 10,
            FromArchetypeId = 1,
            ToArchetypeId = 2,
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            WindowSize = 20,
            TransitionCount = 12,
            TransitionProbability = 0.75
        });
        await db.SaveChangesAsync();

        var service = new TransitionService(db, new StubArchetypeService(), NullLogger<TransitionService>.Instance);
        var matrix = await service.GetTransitionMatrixAsync("BTCUSDT", "1h", 20, default);
        var outgoing = await service.GetTransitionsFromAsync(1, 10, default);
        var prediction = await service.PredictNextAsync("BTCUSDT", "1h", 20, default);
        var entropy = await service.GetEntropyRankingAsync("BTCUSDT", "1h", 20, 10, default);

        Assert.Equal(2, matrix.ArchetypeCount);
        Assert.Equal(12, matrix.TotalTransitions);
        var cell = Assert.Single(matrix.Cells);
        Assert.Equal("A", cell.FromCode);
        Assert.Equal("B", cell.ToCode);
        Assert.Equal("B", Assert.Single(outgoing.Transitions).ToArchetypeCode);
        Assert.False(prediction.Validated);
        Assert.Equal("Unavailable", prediction.Predictability);
        Assert.Contains("out-of-sample", prediction.Reason);
        Assert.False(entropy.Validated);
        Assert.Equal("Experimental", entropy.Maturity);
        var entropyItem = Assert.Single(entropy.Items);
        Assert.False(entropyItem.Validated);
        Assert.Equal("Unavailable", entropyItem.Predictability);
        Assert.Contains("out-of-sample", entropyItem.Reason);

        var json = JsonSerializer.Serialize(matrix, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"cells\"", json);
        Assert.Contains("\"fromCode\":\"A\"", json);
    }

    [Fact]
    public async Task PredictSequence_IsExplicitlyUnvalidated()
    {
        await using var db = CreateDb();
        var service = new TransitionService(db, new StubArchetypeService(), NullLogger<TransitionService>.Instance);

        var result = await service.GetSequencePredictionAsync("BTCUSDT", "1h", 20, default);

        Assert.False(result.Validated);
        Assert.Empty(result.TopSequences);
        Assert.Contains("does not persist", result.Reason);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class StubArchetypeService : IArchetypeService
    {
        public Task<ArchetypeMatchDto?> MatchCurrentWindowAsync(string symbol, string timeframe, int windowSize, CancellationToken ct = default) =>
            Task.FromResult<ArchetypeMatchDto?>(new ArchetypeMatchDto
            {
                Similarity = 0.9f,
                Archetype = new ArchetypeDto { Id = 1, ArchetypeCode = "A" }
            });

        public Task<(int Total, List<ArchetypeDto> Items)> GetArchetypesAsync(string symbol, string timeframe, int? windowSize, string sortBy, int page, int pageSize, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ArchetypeDetailDto?> GetArchetypeDetailAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(List<ArchetypeMatchDto> Matches, object WeightedSignal)> MatchMultiWindowAsync(string symbol, string timeframe, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int Total, List<ArchetypeOccurrenceDto> Items)> GetOccurrencesAsync(long archetypeId, string horizon, int page, int pageSize, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<ArchetypeRankingDto>> GetRankingsAsync(string symbol, string timeframe, int? windowSize, string? horizon, string sortBy, int top, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
