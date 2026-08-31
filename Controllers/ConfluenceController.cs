using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConfluenceController : ControllerBase
{
    private readonly IConfluenceService _confluenceService;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CurrentTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HistoryTtl = TimeSpan.FromSeconds(15);

    [ActivatorUtilitiesConstructor]
    public ConfluenceController(IConfluenceService confluenceService, IMemoryCache cache)
    {
        _confluenceService = confluenceService;
        _cache = cache;
    }

    public ConfluenceController(IConfluenceService confluenceService)
        : this(confluenceService, new MemoryCache(new MemoryCacheOptions()))
    {
    }

    [HttpGet("current")]
    public async Task<IActionResult> GetCurrent([FromQuery] string symbol = "BTCUSDT", CancellationToken ct = default)
    {
        var cacheKey = $"confluence:current:{symbol.ToUpperInvariant()}";
        if (_cache.TryGetValue(cacheKey, out ConfluenceSnapshot? cached) && cached != null)
        {
            return Ok(MapToDto(cached));
        }

        var snapshot = await _confluenceService.GetLatestConfluenceAsync(symbol, ct);
        if (snapshot == null) return NotFound();

        _cache.Set(cacheKey, snapshot, CurrentTtl);
        return Ok(MapToDto(snapshot));
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory([FromQuery] string symbol = "BTCUSDT", [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var cacheKey = $"confluence:history:{symbol.ToUpperInvariant()}:{limit}";
        if (_cache.TryGetValue(cacheKey, out List<ConfluenceSnapshot>? cached) && cached != null)
        {
            return Ok(cached.Select(MapToDto));
        }

        var history = await _confluenceService.GetConfluenceHistoryAsync(symbol, limit, ct);
        _cache.Set(cacheKey, history, HistoryTtl);
        return Ok(history.Select(MapToDto));
    }

    [HttpPost("calculate")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> Calculate([FromQuery] string symbol = "BTCUSDT", CancellationToken ct = default)
    {
        var snapshot = await _confluenceService.CalculateConfluenceAsync(symbol, ct);
        return Ok(MapToDto(snapshot));
    }

    internal static ConfluenceSnapshotDto MapToDto(ConfluenceSnapshot snapshot)
    {
        List<ConfluenceTimeframeAlignmentDto> alignments;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(snapshot.TimeframeAlignmentsJson);
            alignments = document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                    .Where(x => x.ValueKind == System.Text.Json.JsonValueKind.Object)
                    .Select(MapAlignment)
                    .ToList()
                : [];
        }
        catch (System.Text.Json.JsonException)
        {
            alignments = [];
        }

        return new ConfluenceSnapshotDto
        {
            Id = snapshot.Id,
            Symbol = snapshot.Symbol,
            TimeMs = snapshot.TimeMs,
            ConfluenceScore = snapshot.ConfluenceScore,
            OverallDirection = snapshot.OverallDirection,
            HasConflict = snapshot.HasConflict,
            ConflictDetails = snapshot.ConflictDetails,
            CreatedAtUtc = snapshot.CreatedAtUtc,
            TimeframeAlignments = alignments
        };
    }

    private static ConfluenceTimeframeAlignmentDto MapAlignment(System.Text.Json.JsonElement item)
    {
        var score = ReadDouble(item, "directionalScore", "DirectionalScore", "Score");
        return new ConfluenceTimeframeAlignmentDto
        {
            Timeframe = ReadString(item, "timeframe", "Timeframe") ?? "",
            Weight = ReadDouble(item, "weight", "Weight"),
            DirectionalScore = score,
            Direction = ReadString(item, "direction", "Direction")
                ?? (score > 0 ? "Bullish" : score < 0 ? "Bearish" : "Neutral"),
            RegimeType = ReadString(item, "regimeType", "RegimeType", "Regime") ?? "Unknown",
            ArchetypeCode = ReadString(item, "archetypeCode", "ArchetypeCode", "Archetype")
        };
    }

    private static string? ReadString(System.Text.Json.JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private static double ReadDouble(System.Text.Json.JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value) && value.TryGetDouble(out var number))
                return number;
        }
        return 0;
    }
}
