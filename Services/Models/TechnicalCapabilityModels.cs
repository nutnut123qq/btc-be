using Backend.Data;
using System.ComponentModel.DataAnnotations;

namespace Backend.Services.Models;

public static class CapabilityOperationalStatuses
{
    public const string Operational = "operational";
    public const string Degraded = "degraded";
    public const string Unavailable = "unavailable";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Operational,
        Degraded,
        Unavailable
    };
}

public static class CapabilityEvidenceStages
{
    public const string Descriptive = "descriptive";
    public const string Experimental = "experimental";
    public const string Validated = "validated";
    public const string ForwardObserved = "forward-observed";
    public const string Retired = "retired";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Descriptive,
        Experimental,
        Validated,
        ForwardObserved,
        Retired
    };
}

public static class CapabilityEvidenceTargets
{
    public const string DataIntegrity = "data-integrity";
    public const string CalculationCorrectness = "calculation-correctness";
    public const string Predictive = "predictive";
    public const string EconomicSimulation = "economic-simulation";
    public const string ProspectiveObservation = "prospective-observation";
    public const string OperationalDelivery = "operational-delivery";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        DataIntegrity,
        CalculationCorrectness,
        Predictive,
        EconomicSimulation,
        ProspectiveObservation,
        OperationalDelivery
    };
}

public sealed record TechnicalCapabilityDto(
    [property: Required] string Id,
    [property: Required] string Name,
    [property: Required] string Category,
    [property: Required] string OperationalStatus,
    [property: Required] string EvidenceStage,
    [property: Required] string EvidenceTarget,
    [property: Required] string IntendedUse,
    [property: Required] string Limitation,
    [property: Required] string Endpoint,
    [property: Required] string Version);

public sealed record TechnicalCapabilitiesResponse(
    [property: Required] string ContractVersion,
    [property: Required] string Symbol,
    [property: Required] DateTime GeneratedAtUtc,
    [property: Required] string EvidenceCatalogEndpoint,
    [property: Required] IReadOnlyList<TechnicalCapabilityDto> Items)
{
    public static TechnicalCapabilitiesResponse Create(DateTime generatedAtUtc, IReadOnlyList<TechnicalCapabilityDto> items) =>
        new(ResearchVersions.TechnicalCapabilityRegistry, "BTCUSDT", generatedAtUtc, "/api/research/evidence", items);
}
