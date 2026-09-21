using Backend.Controllers;
using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Backend.Tests;

public sealed class TechnicalCapabilityRegistryTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Snapshot_CoversTechnicalModules_WithStableControlledMetadata()
    {
        var snapshot = new TechnicalCapabilityRegistry(new FixedTimeProvider(FixedNow)).GetSnapshot();

        Assert.Equal(ResearchVersions.TechnicalCapabilityRegistry, snapshot.ContractVersion);
        Assert.Equal("BTCUSDT", snapshot.Symbol);
        Assert.Equal(FixedNow.UtcDateTime, snapshot.GeneratedAtUtc);
        Assert.Equal("/api/research/evidence", snapshot.EvidenceCatalogEndpoint);
        Assert.Equal(20, snapshot.Items.Count);
        Assert.Equal(snapshot.Items.Count, snapshot.Items.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(snapshot.Items, item =>
        {
            Assert.Contains(item.OperationalStatus, CapabilityOperationalStatuses.All);
            Assert.Contains(item.EvidenceStage, CapabilityEvidenceStages.All);
            Assert.Contains(item.EvidenceTarget, CapabilityEvidenceTargets.All);
            Assert.StartsWith("/api/", item.Endpoint, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(item.IntendedUse));
            Assert.False(string.IsNullOrWhiteSpace(item.Limitation));
            Assert.False(string.IsNullOrWhiteSpace(item.Version));
        });

        Assert.Contains(snapshot.Items, x => x.Id == "market-data" && x.EvidenceStage == "validated");
        Assert.Contains(snapshot.Items, x => x.Id == "market-data" && x.EvidenceTarget == "data-integrity");
        Assert.Contains(snapshot.Items, x => x.Id == "historical-replay" && x.EvidenceTarget == "economic-simulation");
        Assert.Contains(snapshot.Items, x => x.Id == "forward-paper" && x.EvidenceTarget == "prospective-observation");
        Assert.Contains(snapshot.Items, x => x.Id == "historical-analog" && x.EvidenceStage == "experimental");
        Assert.Contains(snapshot.Items, x => x.Id == "markov-transitions" && x.OperationalStatus == "unavailable");
        Assert.Contains(snapshot.Items, x => x.Id == "forward-paper"
            && x.OperationalStatus == "operational"
            && x.EvidenceStage == "experimental"
            && x.Endpoint == "/api/paper-observations"
            && x.IntendedUse.Contains("latest finalized", StringComparison.OrdinalIgnoreCase)
            && x.Limitation.Contains("does not backfill", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Controller_ReturnsRegistrySnapshot()
    {
        var registry = new TechnicalCapabilityRegistry(new FixedTimeProvider(FixedNow));
        var result = new ResearchCapabilitiesController(registry).Get();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<TechnicalCapabilitiesResponse>(ok.Value);
        Assert.Equal(20, body.Items.Count);
        Assert.Equal(ResearchVersions.TechnicalCapabilityRegistry, body.ContractVersion);
    }

    [Fact]
    public void ForwardPaperCapability_UsesAnActualControllerRoute()
    {
        var endpoint = new TechnicalCapabilityRegistry(new FixedTimeProvider(FixedNow))
            .GetSnapshot().Items.Single(x => x.Id == "forward-paper").Endpoint;
        var routes = typeof(PaperObservationsController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: true)
            .Cast<RouteAttribute>()
            .Select(x => "/" + x.Template)
            .ToArray();

        Assert.Contains(endpoint, routes, StringComparer.Ordinal);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
