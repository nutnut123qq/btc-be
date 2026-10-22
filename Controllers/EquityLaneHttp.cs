using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

/// <summary>
/// Shared HTTP glue for the equity lane: the /api/equities controllers and the fixed-MBB
/// /api/mbb alias controllers build identical envelopes and projections from here.
/// </summary>
internal static class EquityLaneHttp
{
    public static ObjectResult Error(ControllerBase controller, int statusCode, string code, string message,
        bool retryable = false) =>
        controller.StatusCode(statusCode, new ApiErrorEnvelope
        {
            Code = code,
            Message = message,
            Retryable = retryable,
            RequestId = controller.HttpContext.TraceIdentifier,
        });

    public static ObjectResult ScopeError(ControllerBase controller, EquityScopeResult scope) =>
        Error(controller, scope.StatusCode, scope.ErrorCode!, scope.ErrorMessage!);

    public static ObjectResult InvalidTimeframe(ControllerBase controller, InstrumentDescriptor instrument, string? timeframe) =>
        Error(controller, StatusCodes.Status400BadRequest, EquityScope.InvalidTimeframe,
            EquityScope.InvalidTimeframeMessage(instrument, timeframe));

    public static ObjectResult LaneBusy(ControllerBase controller, string symbol) =>
        Error(controller, StatusCodes.Status409Conflict, EquityScope.LaneBusy,
            $"The {symbol} lane is busy (sync/backfill/recompute/observe in progress); retry shortly.",
            retryable: true);

    public static MbbCandleDto ToCandleDto(InstrumentCandle c) => new()
    {
        OpenTimeMs = c.OpenTimeMs,
        TimeIso = DateTimeOffset.FromUnixTimeMilliseconds(c.OpenTimeMs).UtcDateTime.ToString("O"),
        Open = c.Open,
        High = c.High,
        Low = c.Low,
        Close = c.Close,
        Volume = c.Volume,
        CloseTimeMs = c.CloseTimeMs,
        // Equity providers do not publish these; null = absent, not zero.
        QuoteVolume = null,
        TradeCount = null,
        TakerBuyVolume = null,
        TakerBuyQuoteVolume = null,
    };

    public static MbbObservationDto ToObservationDto(MbbSignalObservation o) => new()
    {
        DecisionId = o.DecisionId,
        RecorderVersion = o.RecorderVersion,
        Symbol = o.Symbol,
        Timeframe = o.Timeframe,
        SignalBarOpenTimeMs = o.SignalBarOpenTimeMs,
        SignalBarCloseTimeMs = o.SignalBarCloseTimeMs,
        ObservedAtUtc = o.ObservedAtUtc,
        Decision = o.Decision,
        EvidenceStatus = o.EvidenceStatus,
        PriceBasis = o.PriceBasis,
        MarketCloseVnd = o.MarketCloseVnd,
        FillPrice = o.FillPrice,
        FillObservedAtUtc = o.FillObservedAtUtc,
        FillSource = o.FillSource,
        FillBarOpenTimeMs = o.FillBarOpenTimeMs,
        OutcomeReturn = o.OutcomeReturn,
        OutcomeObservedAtUtc = o.OutcomeObservedAtUtc,
        OutcomeHorizon = o.OutcomeHorizon,
    };

    public static bool TryParseLocalDate(ControllerBase controller, string? raw, out DateOnly? date,
        out ObjectResult? error)
    {
        date = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", out var parsed))
        {
            date = parsed;
            return true;
        }
        error = Error(controller, StatusCodes.Status400BadRequest, "INVALID_DATE",
            $"Expected a yyyy-MM-dd HOSE local date, got '{raw}'.");
        return false;
    }
}
