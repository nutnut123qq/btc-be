using System.ComponentModel.DataAnnotations;

namespace Backend.Services.Models;

public sealed record PaperObservationDto(
    [property: Required] Guid Id,
    [property: Required] string DecisionId,
    [property: Required] string RecorderVersion,
    [property: Required] string Symbol,
    [property: Required] string Timeframe,
    [property: Required] long SignalBarOpenTimeMs,
    [property: Required] long SignalBarCloseTimeMs,
    [property: Required] DateTime ObservedAtUtc,
    [property: Required] long AvailableTimeMs,
    string? ModelVersion,
    [property: Required] string Decision,
    double? Confidence,
    string? AbstentionReason,
    [property: Required] string QuoteSource,
    decimal? QuotePrice,
    DateTime? QuoteReceivedAtUtc,
    long? QuoteReceivedTimeMs,
    [property: Required] string ConfigProvenanceJson,
    [property: Required] string EvidenceProvenanceJson,
    decimal? FillPrice,
    DateTime? FillObservedAtUtc,
    double? OutcomeReturn,
    DateTime? OutcomeObservedAtUtc,
    string? OutcomeHorizon);

public sealed record PaperObservationListResponse(
    [property: Required] string Symbol,
    [property: Required] bool Available,
    string? Reason,
    [property: Required] IReadOnlyList<PaperObservationDto> Items);
