using Backend.Controllers;
using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class ConfluenceContractTests
{
    [Theory]
    [InlineData("[{\"Timeframe\":\"1h\",\"Score\":0.4,\"Regime\":\"TrendingUp\",\"Archetype\":\"A\",\"Weight\":0.2}]")]
    [InlineData("[{\"timeframe\":\"1h\",\"directionalScore\":0.4,\"direction\":\"Bullish\",\"regimeType\":\"TrendingUp\",\"archetypeCode\":\"A\",\"weight\":0.2}]")]
    public void MapToDto_AcceptsLegacyAndCanonicalAlignments(string json)
    {
        var result = ConfluenceController.MapToDto(new ConfluenceSnapshot { TimeframeAlignmentsJson = json });

        var item = Assert.Single(result.TimeframeAlignments);
        Assert.Equal("1h", item.Timeframe);
        Assert.Equal("Bullish", item.Direction);
        Assert.Equal(0.4, item.DirectionalScore);
        Assert.Equal("TrendingUp", item.RegimeType);
        Assert.Equal("A", item.ArchetypeCode);
    }

    [Fact]
    public void MapToDto_MalformedJsonReturnsEmptyAlignments()
    {
        var result = ConfluenceController.MapToDto(new ConfluenceSnapshot { TimeframeAlignmentsJson = "not-json" });
        Assert.Empty(result.TimeframeAlignments);
    }

    [Fact]
    public async Task CalculateConfluence_UsesSupportedWindow20AndPersistsArchetypeCode()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);
        var archetypes = new RecordingArchetypeService();
        var service = new ConfluenceService(
            archetypes,
            new StubRegimeService(),
            null!,
            db,
            NullLogger<ConfluenceService>.Instance);

        var snapshot = await service.CalculateConfluenceAsync("BTCUSDT");
        var dto = ConfluenceController.MapToDto(snapshot);

        Assert.Equal(4, archetypes.WindowSizes.Count);
        Assert.All(archetypes.WindowSizes, size => Assert.Equal(20, size));
        Assert.All(dto.TimeframeAlignments, item => Assert.Equal("A20", item.ArchetypeCode));
    }

    private sealed class RecordingArchetypeService : IArchetypeService
    {
        public List<int> WindowSizes { get; } = [];

        public Task<ArchetypeMatchDto?> MatchCurrentWindowAsync(string symbol, string timeframe, int windowSize, CancellationToken ct = default)
        {
            WindowSizes.Add(windowSize);
            return Task.FromResult<ArchetypeMatchDto?>(new ArchetypeMatchDto
            {
                Archetype = new ArchetypeDto { ArchetypeCode = $"A{windowSize}" }
            });
        }

        public Task<(int Total, List<ArchetypeDto> Items)> GetArchetypesAsync(string symbol, string timeframe, int? windowSize, string sortBy, int page, int pageSize, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ArchetypeDetailDto?> GetArchetypeDetailAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(List<ArchetypeMatchDto> Matches, object WeightedSignal)> MatchMultiWindowAsync(string symbol, string timeframe, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int Total, List<ArchetypeOccurrenceDto> Items)> GetOccurrencesAsync(long archetypeId, string horizon, int page, int pageSize, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<ArchetypeRankingDto>> GetRankingsAsync(string symbol, string timeframe, int? windowSize, string? horizon, string sortBy, int top, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubRegimeService : IRegimeDetectionService
    {
        public Task<MarketRegime?> GetCurrentRegimeAsync(string symbol, string timeframe, CancellationToken ct = default) =>
            Task.FromResult<MarketRegime?>(new MarketRegime { RegimeType = "TrendingUp" });
        public Task<List<MarketRegime>> GetRegimeHistoryAsync(string symbol, string timeframe, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BuildRegimesAsync(string symbol, string timeframe, int lookbackBars, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<object> GetRegimeSummaryAsync(string symbol, string timeframe, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
