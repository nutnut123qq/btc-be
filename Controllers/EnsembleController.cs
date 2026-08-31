using Backend.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EnsembleController : ControllerBase
{
    private readonly IEnsembleService _ensembleService;

    public EnsembleController(IEnsembleService ensembleService)
    {
        _ensembleService = ensembleService;
    }

    [HttpGet("predict")]
    public async Task<IActionResult> PredictEnsemble([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", CancellationToken ct = default)
    {
        var result = await _ensembleService.PredictEnsembleAsync(symbol, timeframe, ct);
        return Ok(MapToDto(result));
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetEnsembleHistory([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", [FromQuery] int limit = 100, [FromQuery] bool includeLegacy = false, CancellationToken ct = default)
    {
        var result = await _ensembleService.GetEnsembleHistoryAsync(symbol, timeframe, limit, includeLegacy, ct);
        return Ok(result.Select(MapToDto));
    }

    [HttpPost("evaluate")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> EvaluatePredictions([FromQuery] string symbol = "BTCUSDT", [FromQuery] int limit = 100, [FromQuery] bool includeLegacy = false, CancellationToken ct = default)
    {
        var result = await _ensembleService.EvaluatePredictionsAsync(symbol, limit, includeLegacy, ct);
        return Ok(new
        {
            result.Symbol,
            result.TotalPredictions,
            result.TrueCount,
            result.FalseCount,
            result.PendingCount,
            result.WinRatePct,
            result.CanonicalEvaluatedCount,
            result.CanonicalTrueCount,
            result.CanonicalFalseCount,
            result.CanonicalPendingCount,
            result.CanonicalWinRatePct,
            result.ReevaluatedCount,
            result.ReevaluatedTrueCount,
            result.ReevaluatedFalseCount,
            result.ReevaluatedPendingCount,
            result.ReevaluatedWinRatePct,
            Validated = false,
            Maturity = "Experimental",
            PromotionEligible = false,
            PromotionReason = "The ensemble has not passed promotion gates; historical accuracy remains visible for review.",
            Items = result.Items.Select(MapToDto),
            ReevaluatedItems = result.ReevaluatedItems.Select(MapToDto)
        });
    }

    [HttpGet("evaluations")]
    public async Task<IActionResult> GetEvaluations([FromQuery] string symbol = "BTCUSDT", [FromQuery] int limit = 100, [FromQuery] bool includeLegacy = false, CancellationToken ct = default)
    {
        var result = await _ensembleService.GetPredictionEvaluationSummaryAsync(symbol, limit, includeLegacy, ct);
        return Ok(new
        {
            result.Symbol,
            result.TotalPredictions,
            result.TrueCount,
            result.FalseCount,
            result.PendingCount,
            result.WinRatePct,
            result.CanonicalEvaluatedCount,
            result.CanonicalTrueCount,
            result.CanonicalFalseCount,
            result.CanonicalPendingCount,
            result.CanonicalWinRatePct,
            result.ReevaluatedCount,
            result.ReevaluatedTrueCount,
            result.ReevaluatedFalseCount,
            result.ReevaluatedPendingCount,
            result.ReevaluatedWinRatePct,
            Validated = false,
            Maturity = "Experimental",
            PromotionEligible = false,
            PromotionReason = "The ensemble has not passed promotion gates; historical accuracy remains visible for review.",
            Items = result.Items.Select(MapToDto),
            ReevaluatedItems = result.ReevaluatedItems.Select(MapToDto)
        });
    }

    [HttpPost("batch-replay")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> BatchReplay([FromQuery] int sampleCount = 2000, [FromQuery] double minConfidence = 0.60, [FromQuery] bool enableMtfFilter = true, [FromQuery] bool enableSmcFilter = true, [FromQuery] bool enableAtrRrEngine = true, [FromQuery] bool enableVolumeFilter = true, [FromQuery] bool enableMlClassifier = true, [FromQuery] bool enableKellySizing = true, [FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", CancellationToken ct = default)
    {
        var result = await _ensembleService.BatchReplayAsync(sampleCount, minConfidence, enableMtfFilter, enableSmcFilter, enableAtrRrEngine, enableVolumeFilter, enableMlClassifier, enableKellySizing, symbol, timeframe, ct);
        return Ok(result); // Already a clean DTO
    }

    private static object MapToDto(Backend.Data.EnsemblePredictionRecord r)
    {
        var layers = ParseLayers(r.LayerBreakdownJson);
        return new
        {
            r.Id,
            r.Symbol,
            r.Timeframe,
            r.TimeMs,
            r.EntryPrice,
            r.FinalDirection,
            r.ProbUp,
            r.ProbDown,
            r.ProbSideways,
            r.EnsembleConfidence,
            r.ActualPrice24h,
            r.ActualReturnPct,
            r.EvaluationStatus,
            r.EvaluatedAtMs,
            r.SourcePredictionId,
            r.PipelineVersion,
            r.EvaluationVersion,
            r.ValidityStatus,
            r.InvalidReason,
            r.ArchivedAtUtc,
            Validated = false,
            Maturity = "Experimental",
            PromotionEligible = false,
            PromotionReason = "This record is research-only and has not passed ensemble promotion gates.",
            r.CreatedAtUtc,
            layers
        };
    }

    internal sealed record EnsembleLayerDto(
        string LayerName,
        double Weight,
        string Direction,
        double ProbUp,
        double ProbDown,
        double ProbSideways,
        string Summary);

    internal static EnsembleLayerDto[] ParseLayers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var layers = root.ValueKind == JsonValueKind.Array
                ? root
                : root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("layers", out var nested)
                    && nested.ValueKind == JsonValueKind.Array
                        ? nested
                        : default;

            if (layers.ValueKind != JsonValueKind.Array) return [];

            return layers.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item =>
                {
                    var probUp = ReadDouble(item, "probUp");
                    var probDown = ReadDouble(item, "probDown");
                    var probSideways = HasNumber(item, "probSideways")
                        ? ReadDouble(item, "probSideways")
                        : Math.Max(0, 1 - probUp - probDown);
                    var direction = ReadString(item, "direction");
                    if (string.IsNullOrWhiteSpace(direction))
                    {
                        direction = probUp > probDown && probUp > probSideways
                            ? "Bullish"
                            : probDown > probUp && probDown > probSideways
                                ? "Bearish"
                                : "Sideways";
                    }

                    var weight = HasNumber(item, "weight")
                        ? ReadDouble(item, "weight")
                        : HasNumber(item, "normalizedWeight")
                            ? ReadDouble(item, "normalizedWeight")
                            : ReadDouble(item, "baseWeight");

                    return new EnsembleLayerDto(
                        ReadString(item, "layerName") ?? "Layer",
                        weight,
                        direction,
                        probUp,
                        probDown,
                        probSideways,
                        ReadString(item, "summary") ?? string.Empty);
                })
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool HasNumber(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number;

    private static double ReadDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var number) ? number : 0;

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
