using Backend.Controllers;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Xunit;

namespace Backend.Tests;

public sealed class ResearchEvidenceCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"btc-evidence-{Guid.NewGuid():N}");
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 21, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Catalog_PublishesVerifiedReport_AndNormalizesSafeDetail()
    {
        var id = WriteEmbeddedReport("ml-v2", tamperAfterHash: false);
        var service = CreateService();

        var catalog = service.GetCatalog();
        var item = Assert.Single(catalog.Items);
        Assert.Equal("BTCUSDT", catalog.Symbol);
        Assert.Equal(id, item.Id);
        Assert.Equal("model", item.Kind);
        Assert.Equal("supported", item.Status);
        Assert.Equal("predictive", item.EvidenceTier);
        Assert.True(item.Integrity.Verified);
        Assert.True(item.Integrity.ManifestHashVerified);
        Assert.True(item.Integrity.ReportHashVerified);
        Assert.Equal(1, catalog.Integrity.ScannedArtifactCount);
        Assert.Equal(0, catalog.Integrity.RejectedArtifactCount);

        var detail = Assert.IsType<ResearchEvidenceDetailDto>(service.GetDetail(id));
        Assert.Equal("postgresql-readonly", detail.Dataset.Source);
        Assert.Equal(1200, detail.Dataset.RowCount);
        Assert.Equal("candidate", detail.Metrics.Single(x => x.Name == "candidate.brier").Name.Split('.')[0]);
        Assert.DoesNotContain("localhost", JsonSerializer.Serialize(detail), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", JsonSerializer.Serialize(detail), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanonicalHash_MatchesFixtureSerializer()
    {
        var payload = new SortedDictionary<string, object?>
        {
            ["a"] = 1,
            ["nested"] = new SortedDictionary<string, object?> { ["z"] = "value" }
        };
        var expected = HashCanonical(payload);
        payload["manifestSha256"] = expected;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));

        Assert.Equal(expected, ResearchEvidenceCatalog.ComputeCanonicalHash(document.RootElement, "manifestSha256"));
    }

    [Fact]
    public void Catalog_FailsClosed_WhenEmbeddedReportHashIsTampered()
    {
        var id = WriteEmbeddedReport("ml-v2", tamperAfterHash: true);
        var catalog = CreateService().GetCatalog();

        Assert.Empty(catalog.Items);
        Assert.Equal(1, catalog.Integrity.ScannedArtifactCount);
        Assert.Equal(1, catalog.Integrity.RejectedArtifactCount);
        Assert.Null(CreateService().GetDetail(id));
    }

    [Fact]
    public void Catalog_DoesNotExposeUnsafeSourceDescriptors()
    {
        var id = WriteEmbeddedReport(
            "ml-v2",
            tamperAfterHash: false,
            source: "postgresql://user:password@localhost/database");

        var detail = Assert.IsType<ResearchEvidenceDetailDto>(CreateService().GetDetail(id));
        var json = JsonSerializer.Serialize(detail);
        Assert.Equal("versioned immutable research input", detail.Dataset.Source);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localhost", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_ExposesManifestBoundLegacyAblation_AsIntegrityLimited()
    {
        var id = WriteSeparateManifestReport();
        var item = Assert.Single(CreateService().GetCatalog().Items);

        Assert.Equal(id, item.Id);
        Assert.Equal("feature", item.Kind);
        Assert.Equal("integrity-limited", item.Status);
        Assert.False(item.Integrity.Verified);
        Assert.True(item.Integrity.ManifestHashVerified);
        Assert.False(item.Integrity.ReportHashVerified);
        Assert.False(item.Integrity.ReportHashEmbedded);
        Assert.Contains(item.Limitations, x => x.Contains("did not embed a report hash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Catalog_VerifiesEveryV3BundleArtifact_AndPublishesArtifactMetadata()
    {
        var id = WriteV3Bundle(tamperPredictions: false);
        var item = Assert.Single(CreateService().GetCatalog().Items);
        var detail = Assert.IsType<ResearchEvidenceDetailDto>(CreateService().GetDetail(id));

        Assert.Equal("model", item.Kind);
        Assert.Equal("inconclusive", item.Status);
        Assert.Equal("retrospective-selection-aware", item.EvidenceTier);
        Assert.True(item.Integrity.Verified);
        Assert.Equal("bundle-manifest-all-artifacts-and-report-payload", item.Integrity.VerificationMode);
        Assert.Equal(3, detail.Artifacts.Count);
        Assert.Equal(3, detail.Artifacts.Single(x => x.Role == "rowPredictions").RowCount);
        Assert.Equal(4, detail.Artifacts.Single(x => x.Role == "datasetSnapshot").RowCount);
        Assert.Contains(detail.Metrics, x => x.Name == "hist_gradient_boosting.brier" && x.Value == 0.4);
        Assert.Contains(detail.Metrics, x => x.Name == "hist_gradient_boosting.brier" && x.Baseline == "adaptive");
        Assert.Contains(detail.Metrics, x => x.Name == "hist_gradient_boosting.logLoss" && x.Baseline == "historical");
        Assert.Contains(detail.Metrics, x => x.Name == "hist_gradient_boosting.brier" && x.Label.Contains("vs adaptive", StringComparison.Ordinal));
        Assert.Contains(detail.Metrics, x => x.Name == "hist_gradient_boosting.logLoss" && x.Label.Contains("vs historical", StringComparison.Ordinal));
        Assert.Contains(detail.Metrics, x => x.Name == "adaptive.brier" && x.Value == 0.5);
        Assert.Contains(detail.Metrics, x => x.Name == "historical.logLoss" && x.Value == 0.85);
        Assert.Equal("Bonferroni over every declared baseline and both proper scoring metrics", detail.Protocol.MultipleTesting);
        Assert.Contains(detail.Findings, x => x.Id == "baseline:adaptive:brier:block:12"
            && x.Status == "positive" && x.Value == 0.1 && x.Lower == 0.05
            && x.Upper is { } upper && Math.Abs(upper - 0.15) < 1e-9
            && x.SampleSize == 3);
        Assert.Contains(detail.Findings, x => x.Id == "baseline:historical:logLoss:block:12"
            && x.MetricName == "logLossLift" && x.Status == "positive");
        Assert.Contains(detail.Findings, x => x.Id == "gate:minimumSamples"
            && x.Status == "passed" && x.Value == 1d);
        Assert.Contains(detail.Findings, x => x.Id == "hgb-ablation:momentum"
            && x.Status == "useful" && x.SampleSize == 3);
        Assert.Contains("not an independent confirmatory result", detail.Conclusion, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_FailsClosed_WhenAnyV3BundleArtifactIsTampered()
    {
        WriteV3Bundle(tamperPredictions: true);
        var catalog = CreateService().GetCatalog();

        Assert.Empty(catalog.Items);
        Assert.Equal(1, catalog.Integrity.RejectedArtifactCount);
    }

    [Fact]
    public void Catalog_PublishesVerifiedTechnicalDescriptiveBundle_WithoutPredictiveClaim()
    {
        var ids = WriteAtomicTechnicalRun();
        var item = Assert.Single(CreateService().GetCatalog().Items.Where(x => x.Timeframe == "4h"));
        var id = ids[1];
        var detail = Assert.IsType<ResearchEvidenceDetailDto>(CreateService().GetDetail(id));

        Assert.Equal("event", item.Kind);
        Assert.Equal("descriptive", item.EvidenceTier);
        Assert.Equal("inconclusive", item.Status);
        Assert.Equal("4h", item.Timeframe);
        Assert.True(item.Integrity.Verified);
        Assert.False(item.Integrity.ReportHashEmbedded);
        Assert.True(item.Integrity.ReportHashVerified);
        Assert.Equal("content-addressed-manifest-all-artifacts-and-ledger-count", item.Integrity.VerificationMode);
        Assert.Equal(100, detail.Dataset.RowCount);
        Assert.Equal(2, detail.Artifacts.Single(x => x.Role == "ledger").RowCount);
        Assert.Contains(detail.Metrics, x => x.Name == "events.eligible" && x.Value == 1);
        Assert.Contains(detail.Findings, x => x.Id == "BOS_BULL:1:forwardReturn:event"
            && x.Status == "descriptive" && x.Value == 0.01 && x.Lower == 0.005 && x.Upper == 0.015);
        Assert.Contains(detail.Findings, x => x.Id == "BOS_BULL:1:forwardReturn:matchedControlDifference"
            && x.Status == "descriptive-comparison");
        Assert.True(detail.EvidenceProfiles.HasValue);
        var profiles = detail.EvidenceProfiles.Value;
        Assert.Equal("all-eligible-events", profiles.GetProperty("unconditional").GetProperty("population").GetString());
        Assert.Equal(7L, profiles.GetProperty("regimeConditioned").GetProperty("sampleSize").GetInt64());
        Assert.True(detail.StatisticalEvidence.HasValue);
        var statistical = detail.StatisticalEvidence.Value;
        Assert.Equal("moving-block-bootstrap", statistical.GetProperty("method").GetString());
        Assert.Equal(0.0125, statistical.GetProperty("familywise").GetProperty("adjustedPValue").GetDouble());
        Assert.Equal(new[] { "BOS_BULL", "FVG_BEAR" },
            statistical.GetProperty("eventOrder").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("no predictive probability", detail.Conclusion, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("positive_predictive", JsonSerializer.Serialize(detail), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_RoundTripsSensitivityAuditExclusionsAndEventTypeDetail()
    {
        var ids = WriteAtomicTechnicalRun();
        var detail = Assert.IsType<ResearchEvidenceDetailDto>(CreateService().GetDetail(ids[1]));

        Assert.True(detail.SensitivityAudit.HasValue);
        var audit = detail.SensitivityAudit.Value;
        Assert.Equal("one_axis_at_a_time", audit.GetProperty("method").GetString());
        var variant = Assert.Single(audit.GetProperty("variants").EnumerateArray());
        Assert.Equal("rsi-period-21", variant.GetProperty("variantId").GetString());
        Assert.Equal(0.5, variant.GetProperty("eligibleDecisionTimeJaccardVsBaseline").GetDouble());
        Assert.Equal(0.002, variant.GetProperty("horizons").GetProperty("1")
            .GetProperty("metrics").GetProperty("forwardReturn").GetProperty("meanDeltaVsBaseline").GetDouble());

        Assert.True(detail.ReportExclusions.HasValue);
        Assert.Equal(1L, detail.ReportExclusions.Value.GetProperty("unknown_availability").GetInt64());

        Assert.True(detail.EventTypeDetail.HasValue);
        var bosBull = detail.EventTypeDetail.Value.GetProperty("BOS_BULL");
        Assert.Equal(1L, bosBull.GetProperty("horizons").GetProperty("1").GetProperty("matchedControls").GetInt64());
        Assert.Equal(1L, bosBull.GetProperty("lifecycleCoverage").GetProperty("unavailable").GetInt64());
        Assert.True(bosBull.TryGetProperty("timeToFirstTouchBars", out _));

        var json = JsonSerializer.Serialize(detail, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"sensitivityAudit\"", json, StringComparison.Ordinal);
        Assert.Contains("\"reportExclusions\"", json, StringComparison.Ordinal);
        Assert.Contains("\"eventTypeDetail\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_FailsClosed_WhenTechnicalDescriptiveLedgerIsTampered()
    {
        WriteAtomicTechnicalRun(tamper4hLedger: true);
        var catalog = CreateService().GetCatalog();

        Assert.Empty(catalog.Items);
        Assert.Equal(1, catalog.Integrity.RejectedArtifactCount);
    }

    [Fact]
    public void Catalog_FailsClosed_WhenTechnicalDescriptiveBundleClaimsPrediction()
    {
        WriteAtomicTechnicalRun(predictive4h: true);
        var catalog = CreateService().GetCatalog();

        Assert.Empty(catalog.Items);
        Assert.Equal(1, catalog.Integrity.RejectedArtifactCount);
    }

    [Fact]
    public void Catalog_IgnoresOrphanTechnicalManifestOutsideAtomicPointer()
    {
        var orphan = WriteTechnicalDescriptiveBundle(timeframe: "4h", predictiveEvidence: true);
        var referenced = WriteAtomicTechnicalRun();

        var catalog = CreateService().GetCatalog();

        Assert.Equal(3, catalog.Items.Count);
        Assert.DoesNotContain(catalog.Items, x => x.Id == orphan);
        Assert.Equal(referenced.Order(), catalog.Items.Select(x => x.Id).Order());
    }

    [Fact]
    public void Catalog_FailsClosedForWholeRun_WhenAtomicManifestIsMissing()
    {
        var ids = WriteAtomicTechnicalRun();
        File.Delete(Path.Combine(_root, "technical-descriptive", $"{ids[1]}.manifest.json"));

        var catalog = CreateService().GetCatalog();

        Assert.Empty(catalog.Items);
        Assert.Equal(1, catalog.Integrity.RejectedArtifactCount);
    }

    [Fact]
    public void Catalog_ExposesIntegrityVerifiedTechnicalPipelineStatus()
    {
        var ids = WriteAtomicTechnicalRun();
        WritePipelineStatus(ids, tamperAfterHash: false, stale: false);

        var pipeline = Assert.IsType<ResearchEvidencePipelineStatusDto>(CreateService().GetCatalog().Pipeline);
        Assert.Equal("succeeded", pipeline.State);
        Assert.True(pipeline.IntegrityVerified);
        Assert.False(pipeline.Running);
        Assert.Equal(3, pipeline.Timeframes.Count);
        Assert.All(pipeline.Timeframes, value => Assert.True(value.SemanticVerification));
    }

    [Fact]
    public void Catalog_FailsClosed_WhenTechnicalPipelineStatusIsTampered()
    {
        var ids = WriteAtomicTechnicalRun();
        WritePipelineStatus(ids, tamperAfterHash: true, stale: false);

        var pipeline = Assert.IsType<ResearchEvidencePipelineStatusDto>(CreateService().GetCatalog().Pipeline);
        Assert.Equal("unavailable", pipeline.State);
        Assert.False(pipeline.IntegrityVerified);
        Assert.Empty(pipeline.Timeframes);
    }

    [Fact]
    public void Catalog_PublishesMbbAtomicRun_FromSymbolSubdirectory()
    {
        var btcIds = WriteAtomicTechnicalRun();
        var mbbIds = WriteAtomicTechnicalRun(
            symbol: "MBB", timeframes: ["1h", "1d"], subdirectory: "MBB");

        var catalog = CreateService().GetCatalog();

        Assert.Equal(5, catalog.Items.Count);
        Assert.Equal(btcIds.Order(), catalog.Items.Where(x => x.Symbol == "BTCUSDT").Select(x => x.Id).Order());
        var mbbItems = catalog.Items.Where(x => x.Symbol == "MBB").OrderBy(x => x.Timeframe, StringComparer.Ordinal).ToArray();
        Assert.Equal(mbbIds.Order(), mbbItems.Select(x => x.Id).Order());
        Assert.Equal(["1d", "1h"], mbbItems.Select(x => x.Timeframe));
        Assert.All(mbbItems, x => Assert.Equal("descriptive", x.EvidenceTier));
        Assert.Equal("ALL", catalog.Symbol);
    }

    [Fact]
    public void Catalog_FailsClosed_WhenMbbRunIndexDeclaresWrongSymbol()
    {
        WriteAtomicTechnicalRun();
        WriteAtomicTechnicalRun(
            symbol: "MBB", timeframes: ["1h", "1d"], subdirectory: "MBB",
            indexSymbolOverride: "BTCUSDT");

        var catalog = CreateService().GetCatalog();

        Assert.Equal(3, catalog.Items.Count);
        Assert.DoesNotContain(catalog.Items, x => x.Symbol == "MBB");
        Assert.Equal(1, catalog.Integrity.RejectedArtifactCount);
        Assert.Equal("BTCUSDT", catalog.Symbol);
    }

    [Fact]
    public void Catalog_FailsClosed_WhenMbbRunIndexDropsAnInstrumentTimeframe()
    {
        WriteAtomicTechnicalRun();
        WriteAtomicTechnicalRun(symbol: "MBB", timeframes: ["1h"], subdirectory: "MBB");

        var catalog = CreateService().GetCatalog();

        Assert.Equal(3, catalog.Items.Count);
        Assert.DoesNotContain(catalog.Items, x => x.Symbol == "MBB");
        Assert.Equal(1, catalog.Integrity.RejectedArtifactCount);
    }

    [Fact]
    public void Catalog_ExposesPerSymbolPipelineStatuses_WithInstrumentTimeframes()
    {
        var btcIds = WriteAtomicTechnicalRun();
        var mbbIds = WriteAtomicTechnicalRun(
            symbol: "MBB", timeframes: ["1h", "1d"], subdirectory: "MBB");
        WritePipelineStatus(btcIds, tamperAfterHash: false, stale: false);
        WritePipelineStatus(mbbIds, tamperAfterHash: false, stale: false,
            timeframes: ["1h", "1d"], subdirectory: "MBB");

        var catalog = CreateService().GetCatalog();

        var pipeline = Assert.IsType<ResearchEvidencePipelineStatusDto>(catalog.Pipeline);
        Assert.Equal("BTCUSDT", pipeline.Symbol);
        Assert.Equal("succeeded", pipeline.State);
        Assert.Equal(3, pipeline.Timeframes.Count);

        var pipelines = Assert.IsAssignableFrom<IReadOnlyList<ResearchEvidencePipelineStatusDto>>(catalog.Pipelines);
        Assert.Equal(2, pipelines.Count);
        Assert.Equal("BTCUSDT", pipelines[0].Symbol);
        var mbb = Assert.Single(pipelines, x => x.Symbol == "MBB");
        Assert.Equal("succeeded", mbb.State);
        Assert.True(mbb.IntegrityVerified);
        Assert.Equal(new[] { "1d", "1h" }, mbb.Timeframes.Select(x => x.Timeframe));
        Assert.All(mbb.Timeframes, x => Assert.True(x.SemanticVerification));
    }

    [Fact]
    public void Catalog_FailsClosedMbbPipelineStatus_WhenTimeframeIsOutsideInstrumentScope()
    {
        var btcIds = WriteAtomicTechnicalRun();
        var mbbIds = WriteAtomicTechnicalRun(
            symbol: "MBB", timeframes: ["1h", "1d"], subdirectory: "MBB");
        WritePipelineStatus(btcIds, tamperAfterHash: false, stale: false);
        WritePipelineStatus(
            [mbbIds[0], btcIds[1], mbbIds[1]], tamperAfterHash: false, stale: false,
            timeframes: ["1h", "4h", "1d"], subdirectory: "MBB");

        var catalog = CreateService().GetCatalog();

        var pipeline = Assert.IsType<ResearchEvidencePipelineStatusDto>(catalog.Pipeline);
        Assert.Equal("succeeded", pipeline.State);
        var mbb = Assert.Single(catalog.Pipelines!, x => x.Symbol == "MBB");
        Assert.Equal("unavailable", mbb.State);
        Assert.False(mbb.IntegrityVerified);
        Assert.Empty(mbb.Timeframes);
    }

    [Theory]
    [InlineData("../outside.predictions.jsonl")]
    [InlineData("not-content-addressed.predictions.jsonl")]
    public void Catalog_FailsClosed_WhenV3ArtifactFilenameIsUnsafeOrNotContentAddressed(string filename)
    {
        WriteV3Bundle(tamperPredictions: false, predictionFileOverride: filename);
        var catalog = CreateService().GetCatalog();

        Assert.Empty(catalog.Items);
        Assert.Equal(1, catalog.Integrity.ScannedArtifactCount);
        Assert.Equal(1, catalog.Integrity.RejectedArtifactCount);
    }

    [Fact]
    public void Catalog_FailsClosed_WhenV3BundleDeclaresUnexpectedArtifactRole()
    {
        WriteV3Bundle(tamperPredictions: false, addUnexpectedArtifact: true);
        var catalog = CreateService().GetCatalog();

        Assert.Empty(catalog.Items);
        Assert.Equal(1, catalog.Integrity.RejectedArtifactCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Catalog_FailsClosed_WhenRetrospectiveV3ClaimsConfirmationOrPromotion(
        bool confirmatory,
        bool promotionAllowed)
    {
        WriteV3Bundle(
            tamperPredictions: false,
            confirmatory: confirmatory,
            promotionAllowed: promotionAllowed);

        var catalog = CreateService().GetCatalog();
        Assert.Empty(catalog.Items);
        Assert.Equal(1, catalog.Integrity.RejectedArtifactCount);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("not-a-hash")]
    [InlineData("AB79A19C2AA1D49351E959A2A1AC51AC915FAA82EB5DC6CDBC600A516EE84AFB")]
    public void Detail_RejectsNonContentAddressedIds(string id)
    {
        WriteEmbeddedReport("ml-v2", tamperAfterHash: false);
        Assert.Null(CreateService().GetDetail(id));
    }

    [Fact]
    public void Controller_ReturnsCatalogDetailAndNotFound()
    {
        var id = WriteEmbeddedReport("ml-v2", tamperAfterHash: false);
        var controller = new ResearchEvidenceController(CreateService());

        Assert.IsType<OkObjectResult>(controller.GetCatalog().Result);
        Assert.IsType<OkObjectResult>(controller.GetDetail(id).Result);
        Assert.IsType<NotFoundResult>(controller.GetDetail("missing").Result);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(3600)]
    public void Catalog_CachesUntilInterval_ThenReverifies(int idleSeconds)
    {
        var time = new MutableTimeProvider(FixedNow);
        var service = CreateService(time, cacheSeconds: 60);
        var id = WriteEmbeddedReport("ml-v2", tamperAfterHash: false);
        var path = Path.Combine(_root, "ml-v2", $"{id}.report.json");
        var mtime = File.GetLastWriteTimeUtc(path);

        Assert.Single(service.GetCatalog().Items);

        // mtime-preserving tamper keeps the fingerprint identical: the cached
        // result is served until the verification interval elapses.
        WriteEmbeddedReport("ml-v2", tamperAfterHash: true);
        File.SetLastWriteTimeUtc(path, mtime);
        Assert.Single(service.GetCatalog().Items);

        // At or past the interval, however long the idle gap, the request itself
        // re-verifies synchronously: no snapshot that old is ever served.
        time.Advance(TimeSpan.FromSeconds(idleSeconds));
        var catalog = service.GetCatalog();
        Assert.Empty(catalog.Items);
        Assert.Equal(1, catalog.Integrity.RejectedArtifactCount);
    }

    [Fact]
    public void Catalog_Reverifies_WhenArtifactSetChanges()
    {
        var service = CreateService();
        var id = WriteEmbeddedReport("ml-v2", tamperAfterHash: false);
        Assert.Single(service.GetCatalog().Items);

        File.Delete(Path.Combine(_root, "ml-v2", $"{id}.report.json"));
        var catalog = service.GetCatalog();
        Assert.Empty(catalog.Items);
    }

    [Fact]
    public void Catalog_VerifiesSynchronously_WhenFingerprintChangesInsideInterval()
    {
        var time = new MutableTimeProvider(FixedNow);
        var service = CreateService(time, cacheSeconds: 60);
        var id = WriteEmbeddedReport("ml-v2", tamperAfterHash: false);
        var path = Path.Combine(_root, "ml-v2", $"{id}.report.json");
        var mtime = File.GetLastWriteTimeUtc(path);
        Assert.Single(service.GetCatalog().Items);

        time.Advance(TimeSpan.FromSeconds(45));
        WriteEmbeddedReport("ml-v2", tamperAfterHash: true);
        File.SetLastWriteTimeUtc(path, mtime.AddSeconds(1));

        // Inside the refresh-ahead window a moved fingerprint still verifies synchronously.
        var catalog = service.GetCatalog();
        Assert.Empty(catalog.Items);
        Assert.Equal(1, catalog.Integrity.RejectedArtifactCount);
    }

    [Fact]
    public async Task Catalog_RefreshAhead_ServesAtOnce_StartsOneReverification_AndReplacesCache()
    {
        WriteAtomicTechnicalRun();
        var contract = new GatedContract();
        var time = new MutableTimeProvider(FixedNow);
        var service = CreateService(time, cacheSeconds: 60, contract);
        Assert.Equal(3, service.GetCatalog().Items.Count);
        Assert.Equal(1, contract.Verifications);

        // Same-size, mtime-preserving ledger tamper: the fingerprint does not move.
        var ledger = Directory.EnumerateFiles(Path.Combine(_root, "technical-descriptive"), "*.ledger.jsonl").First();
        var ledgerMtime = File.GetLastWriteTimeUtc(ledger);
        File.WriteAllText(ledger, File.ReadAllText(ledger).Replace("\"one\"", "\"uno\""), new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(ledger, ledgerMtime);

        // Before half the interval: plain cache hit, no re-verification.
        contract.Hold();
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(3, service.GetCatalog().Items.Count);
        Assert.False(contract.Entered.Wait(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(1, contract.Verifications);

        // In [interval/2, interval): served at once while exactly one background run starts.
        time.Advance(TimeSpan.FromSeconds(16));
        // Times out (fails) if any request waits for the held re-verification.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => service.GetCatalog())))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, x => Assert.Equal(3, x.Items.Count));
        Assert.True(contract.Entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, contract.Verifications);

        // The finished run replaces the cache (content and age).
        contract.Release();
        var catalog = WaitForCatalog(service, x => x.Items.Count == 0);
        Assert.Empty(catalog.Items);
        Assert.Equal(1, catalog.Integrity.RejectedArtifactCount);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Empty(service.GetCatalog().Items);
        Assert.Equal(2, contract.Verifications);
    }

    [Fact]
    public void Catalog_VerifiesEveryRequest_WhenIntervalIsZero()
    {
        WriteAtomicTechnicalRun();
        var contract = new GatedContract();
        var service = CreateService(cacheSeconds: 0, technicalContract: contract);

        for (var i = 0; i < 3; i++)
            Assert.Equal(3, service.GetCatalog().Items.Count);
        Assert.Equal(3, contract.Verifications);
    }

    [Fact]
    public void Catalog_FailedBackgroundReverification_IsSurfacedByTheNextRequest()
    {
        WriteAtomicTechnicalRun();
        var contract = new GatedContract();
        var time = new MutableTimeProvider(FixedNow);
        var service = CreateService(time, cacheSeconds: 60, contract);
        Assert.Equal(3, service.GetCatalog().Items.Count);

        contract.Failure = new ApplicationException("verifier crashed");
        time.Advance(TimeSpan.FromSeconds(45));
        Assert.Equal(3, service.GetCatalog().Items.Count);

        // The failed run drops the snapshot instead of extending it, so a later
        // request verifies synchronously and the failure reaches the caller.
        Assert.True(SpinWait.SpinUntil(() =>
        {
            try { service.GetCatalog(); return false; }
            catch (ApplicationException) { return true; }
        }, TimeSpan.FromSeconds(10)));

        contract.Failure = null;
        Assert.Equal(3, service.GetCatalog().Items.Count);
    }

    private static ResearchEvidenceCatalogResponse WaitForCatalog(
        ResearchEvidenceCatalog service,
        Func<ResearchEvidenceCatalogResponse, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var catalog = service.GetCatalog();
            if (done(catalog) || DateTime.UtcNow > deadline)
                return catalog;
            Thread.Sleep(10);
        }
    }

    private ResearchEvidenceCatalog CreateService(
        TimeProvider? timeProvider = null,
        int cacheSeconds = 300,
        ITechnicalModuleContractProvider? technicalContract = null) => new(
        new FakeEnvironment { ContentRootPath = _root },
        Microsoft.Extensions.Options.Options.Create(new EvidenceCatalogOptions
        {
            RootPath = ".",
            MaxArtifactBytes = 1_000_000,
            CacheVerificationSeconds = cacheSeconds
        }),
        timeProvider ?? new FixedTimeProvider(FixedNow),
        new TestLogger(),
        technicalContract);

    private string WriteEmbeddedReport(
        string directory,
        bool tamperAfterHash,
        string source = "postgresql-readonly")
    {
        var manifestWithoutHash = new SortedDictionary<string, object?>
        {
            ["codeProvenance"] = new SortedDictionary<string, object?>
            {
                ["evaluatorSha256"] = new string('a', 64),
                ["git"] = new SortedDictionary<string, object?> { ["commit"] = "deadbeef", ["dirty"] = true },
                ["researchContractSha256"] = new string('b', 64)
            },
            ["contractVersion"] = "btc-technical-research-v1",
            ["dataProvenance"] = new SortedDictionary<string, object?>
            {
                ["databaseIdentity"] = new SortedDictionary<string, object?> { ["host"] = "localhost", ["passwordExcluded"] = true },
                ["datasetSha256"] = new string('c', 64),
                ["firstDecisionTimeMs"] = 1000L,
                ["lastDecisionTimeMs"] = 2000L,
                ["rowCount"] = 1200L,
                ["source"] = source
            },
            ["decisionTime"] = "after-signal-bar-close",
            ["experiment"] = "fixture",
            ["outcomePriceBasis"] = "close-to-close",
            ["symbol"] = "BTCUSDT",
            ["timeframe"] = "4h"
        };
        var id = HashCanonical(manifestWithoutHash);
        manifestWithoutHash["manifestSha256"] = id;

        var report = new SortedDictionary<string, object?>
        {
            ["evaluation"] = new SortedDictionary<string, object?>
            {
                ["baselineDefinitions"] = new SortedDictionary<string, object?> { ["prior"] = "available labels" },
                ["coverage"] = 1.0,
                ["evaluationRows"] = 1000,
                ["evaluatorVersion"] = "fixture-v1",
                ["folds"] = Array.Empty<object>(),
                ["promotionGate"] = new SortedDictionary<string, object?> { ["passed"] = true },
                ["selectedCandidate"] = "candidate",
                ["strongestBaseline"] = "prior",
                ["trials"] = new object[]
                {
                    new SortedDictionary<string, object?>
                    {
                        ["coverage"] = 1.0,
                        ["familywiseControl"] = new SortedDictionary<string, object?> { ["confidenceLevel"] = 0.975 },
                        ["metrics"] = new SortedDictionary<string, object?>
                        {
                            ["balancedAccuracy"] = 0.55,
                            ["brier"] = tamperAfterHash ? 0.45 : 0.44,
                            ["logLoss"] = 0.8
                        },
                        ["pairedBrierLift"] = 0.1,
                        ["pairedBrierLiftFamilywiseCi"] = new[] { 0.05, 0.15 },
                        ["trial"] = "candidate"
                    },
                    new SortedDictionary<string, object?>
                    {
                        ["metrics"] = new SortedDictionary<string, object?> { ["balancedAccuracy"] = 0.33, ["brier"] = 0.54, ["logLoss"] = 1.0 },
                        ["trial"] = "prior"
                    }
                }
            },
            ["manifest"] = manifestWithoutHash
        };
        report["reportSha256"] = HashCanonical(report);
        if (tamperAfterHash)
            ((SortedDictionary<string, object?>)((object[])((SortedDictionary<string, object?>)report["evaluation"]!)["trials"]!)[0])["pairedBrierLift"] = 0.2;

        var target = Path.Combine(_root, directory);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, $"{id}.report.json"), JsonSerializer.Serialize(report), new UTF8Encoding(false));
        return id;
    }

    private string WriteSeparateManifestReport()
    {
        var manifest = new SortedDictionary<string, object?>
        {
            ["contractVersion"] = "btc-technical-research-v1",
            ["dataProvenance"] = new SortedDictionary<string, object?> { ["rowCount"] = 1000L, ["source"] = "fixture" },
            ["decisionTime"] = "after-close",
            ["experiment"] = "ablation-fixture",
            ["outcomePriceBasis"] = "close-to-close",
            ["symbol"] = "BTCUSDT",
            ["timeframe"] = "4h"
        };
        var id = HashCanonical(manifest);
        manifest["manifestSha256"] = id;
        var report = new SortedDictionary<string, object?>
        {
            ["evaluation"] = new SortedDictionary<string, object?>
            {
                ["baseline"] = new SortedDictionary<string, object?> { ["brier"] = 0.5 },
                ["evaluationRows"] = 1000L,
                ["fullModel"] = new SortedDictionary<string, object?>
                {
                    ["balancedAccuracy"] = 0.4,
                    ["brier"] = 0.51,
                    ["brierLiftFamilywiseCi"] = new[] { -0.02, 0.01 },
                    ["brierLiftVsRollingPrior"] = -0.01
                },
                ["groups"] = new SortedDictionary<string, object?>(),
                ["protocolCoverage"] = 1.0
            },
            ["manifestSha256"] = id
        };
        var target = Path.Combine(_root, "feature-groups");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, $"{id}.manifest.json"), JsonSerializer.Serialize(manifest), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(target, $"{id}.report.json"), JsonSerializer.Serialize(report), new UTF8Encoding(false));
        return id;
    }

    private string WriteV3Bundle(
        bool tamperPredictions,
        string? predictionFileOverride = null,
        bool addUnexpectedArtifact = false,
        bool confirmatory = false,
        bool promotionAllowed = false)
    {
        var target = Path.Combine(_root, "ml-v3");
        Directory.CreateDirectory(target);
        var researchManifest = new SortedDictionary<string, object?>
        {
            ["codeProvenance"] = new SortedDictionary<string, object?>
            {
                ["git"] = new SortedDictionary<string, object?> { ["commit"] = "deadbeef", ["dirty"] = true },
                ["researchContractSha256"] = new string('b', 64),
                ["v3EvaluatorSha256"] = new string('a', 64)
            },
            ["dataProvenance"] = new SortedDictionary<string, object?>
            {
                ["datasetSha256"] = new string('c', 64),
                ["firstDecisionTimeMs"] = 1000L,
                ["lastDecisionTimeMs"] = 2000L,
                ["rowCount"] = 4L,
                ["source"] = "frozen-fixture"
            },
            ["experiment"] = "v3-fixture",
            ["schemaVersion"] = "btc-ml-evidence-bundle/v3",
            ["symbol"] = "BTCUSDT",
            ["timeframe"] = "4h"
        };
        var researchHash = HashCanonical(researchManifest);
        researchManifest["manifestSha256"] = researchHash;
        var evaluation = new SortedDictionary<string, object?>
        {
            ["baselineComparisons"] = new object[]
            {
                BaselineComparison("adaptive", 0.1, 0.08),
                BaselineComparison("historical", 0.15, 0.05)
            },
            ["baselineDefinitions"] = new SortedDictionary<string, object?>
            {
                ["adaptive"] = "recent available labels",
                ["historical"] = "all labels available at decision time"
            },
            ["evidenceTier"] = "retrospective_selection_aware",
            ["evaluationRows"] = 3L,
            ["evaluatorVersion"] = "btc-4h-next-bar-ml-evidence-v3",
            ["folds"] = new object[] { new SortedDictionary<string, object?> { ["foldId"] = 1 } },
            ["familywiseControl"] = new SortedDictionary<string, object?>
            {
                ["comparisons"] = 4,
                ["familyAlpha"] = 0.05,
                ["method"] = "Bonferroni over every declared baseline and both proper scoring metrics",
                ["perComparisonAlpha"] = 0.0125
            },
            ["gateChecks"] = new SortedDictionary<string, object?>
            {
                ["allBaselineMetricIntervalsPositiveAcrossBlockSizes"] = true,
                ["brierBetterThanStrongestBrierBaseline"] = true,
                ["identicalTimestampCoverage"] = true,
                ["logLossBetterThanStrongestLogLossBaseline"] = true,
                ["minimumClassSupport"] = true,
                ["minimumSamples"] = true
            },
            ["hgbFeatureGroupAblation"] = new object[]
            {
                new SortedDictionary<string, object?>
                {
                    ["comparison"] = "same HGB hyperparameters, folds, calibration partitions, and OOS timestamps as full model",
                    ["familywiseCi"] = new[] { 0.01, 0.03 },
                    ["group"] = "momentum",
                    ["interpretation"] = "useful",
                    ["meanBrierDamageWhenOmitted"] = 0.02,
                    ["metrics"] = new SortedDictionary<string, object?> { ["brier"] = 0.42 },
                    ["omittedFeatures"] = new[] { "Rsi14" },
                    ["trial"] = "hgb_without_momentum"
                }
            },
            ["metrics"] = new SortedDictionary<string, object?>
            {
                ["adaptive"] = new SortedDictionary<string, object?> { ["brier"] = 0.5, ["logLoss"] = 0.9 },
                ["historical"] = new SortedDictionary<string, object?> { ["brier"] = 0.55, ["logLoss"] = 0.85 },
                ["hist_gradient_boosting"] = new SortedDictionary<string, object?>
                {
                    ["balancedAccuracy"] = 0.55, ["brier"] = 0.4, ["logLoss"] = 0.8
                }
            },
            ["pairedBrierSensitivity"] = new object[]
            {
                new SortedDictionary<string, object?>
                {
                    ["blockSizeRows"] = 12L, ["ci"] = new[] { 0.05, 0.15 },
                    ["confidenceLevel"] = 0.95, ["meanPairedBrierLift"] = 0.1
                }
            },
            ["promotionGate"] = new SortedDictionary<string, object?>
            {
                ["confirmatory"] = confirmatory,
                ["passed"] = true,
                ["promotionAllowed"] = promotionAllowed
            },
            ["schemaVersion"] = "btc-ml-evidence-bundle/v3",
            ["strongestBaseline"] = "adaptive",
            ["strongestBaselineByMetric"] = new SortedDictionary<string, object?>
            {
                ["brier"] = "adaptive",
                ["logLoss"] = "historical"
            }
        };
        var report = new SortedDictionary<string, object?>
        {
            ["evaluation"] = evaluation,
            ["researchManifest"] = researchManifest,
            ["schemaVersion"] = "btc-ml-evidence-bundle/v3"
        };
        report["reportPayloadSha256"] = HashCanonical(report);
        var reportBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(report));
        var datasetBytes = Encoding.UTF8.GetBytes("frozen-dataset");
        var predictionBytes = Encoding.UTF8.GetBytes("prediction-one\nprediction-two\nprediction-three\n");
        var reportHash = HashBytes(reportBytes);
        var datasetHash = HashBytes(datasetBytes);
        var predictionsHash = HashBytes(predictionBytes);
        var reportName = $"{reportHash}.report.json";
        var datasetName = $"{datasetHash}.dataset.npz";
        var predictionsName = $"{predictionsHash}.predictions.jsonl";
        File.WriteAllBytes(Path.Combine(target, reportName), reportBytes);
        File.WriteAllBytes(Path.Combine(target, datasetName), datasetBytes);
        File.WriteAllBytes(Path.Combine(target, predictionsName), predictionBytes);

        var bundle = new SortedDictionary<string, object?>
        {
            ["artifacts"] = new SortedDictionary<string, object?>
            {
                ["datasetSnapshot"] = Artifact(datasetName, datasetHash, datasetBytes.Length),
                ["report"] = Artifact(reportName, reportHash, reportBytes.Length),
                ["rowPredictions"] = Artifact(predictionFileOverride ?? predictionsName, predictionsHash, predictionBytes.Length)
            },
            ["integrityPolicy"] = "verify every artifact byte hash before use",
            ["researchManifestSha256"] = researchHash,
            ["schemaVersion"] = "btc-ml-evidence-bundle/v3"
        };
        if (addUnexpectedArtifact)
            ((SortedDictionary<string, object?>)bundle["artifacts"]!)["debugLedger"] =
                Artifact(predictionsName, predictionsHash, predictionBytes.Length);
        var bundleHash = HashCanonical(bundle);
        bundle["bundleManifestSha256"] = bundleHash;
        File.WriteAllText(Path.Combine(target, $"{bundleHash}.manifest.json"), JsonSerializer.Serialize(bundle), new UTF8Encoding(false));
        if (tamperPredictions)
            File.AppendAllText(Path.Combine(target, predictionsName), "tampered", new UTF8Encoding(false));
        return bundleHash;

        static SortedDictionary<string, object?> Artifact(string file, string sha256, int bytes) => new()
        {
            ["bytes"] = bytes,
            ["file"] = file,
            ["sha256"] = sha256
        };

        static SortedDictionary<string, object?> BaselineComparison(
            string baseline,
            double brierLift,
            double logLossLift) => new()
        {
            ["baseline"] = baseline,
            ["metrics"] = new SortedDictionary<string, object?>
            {
                ["brier"] = MetricComparison(brierLift),
                ["logLoss"] = MetricComparison(logLossLift)
            }
        };

        static SortedDictionary<string, object?> MetricComparison(double lift) => new()
        {
            ["candidateBetterPointEstimate"] = true,
            ["intervals"] = new object[]
            {
                new SortedDictionary<string, object?>
                {
                    ["blockSizeRows"] = 12L,
                    ["ci"] = new[] { lift / 2, lift * 1.5 },
                    ["status"] = "positive"
                }
            },
            ["meanLift"] = lift,
            ["robustAcrossBlockSizes"] = true
        };
    }

    private readonly Dictionary<string, (string File, string Hash)> _runIndexByDirectory =
        new(StringComparer.Ordinal);

    private string[] WriteAtomicTechnicalRun(
        bool tamper4hLedger = false,
        bool predictive4h = false,
        string symbol = "BTCUSDT",
        IReadOnlyList<string>? timeframes = null,
        string? subdirectory = null,
        string? indexSymbolOverride = null)
    {
        timeframes ??= ["1h", "4h", "1d"];
        var ids = timeframes
            .Select(timeframe => WriteTechnicalDescriptiveBundle(
                timeframe: timeframe,
                predictiveEvidence: predictive4h && timeframe == "4h",
                symbol: symbol,
                subdirectory: subdirectory))
            .ToArray();
        var target = subdirectory is null
            ? Path.Combine(_root, "technical-descriptive")
            : Path.Combine(_root, "technical-descriptive", subdirectory);
        if (tamper4hLedger)
        {
            var ledgerPath = Directory.EnumerateFiles(target, "*.ledger.jsonl").Single();
            File.AppendAllText(ledgerPath, "{\"tampered\":true}\n", new UTF8Encoding(false));
        }
        var bundles = ids.Select((id, index) => new SortedDictionary<string, object?>
        {
            ["cutoffMs"] = 1_789_948_799_999L,
            ["eligible"] = 1L,
            ["excluded"] = 1L,
            ["manifestFileName"] = $"{id}.manifest.json",
            ["manifestSha256"] = id,
            ["realizedAtMaxHorizon"] = 1L,
            ["semanticVerification"] = true,
            ["stored"] = 2L,
            ["timeframe"] = timeframes[index]
        }).ToArray();
        var indexValue = new SortedDictionary<string, object?>
        {
            ["bundles"] = bundles,
            ["claimType"] = "descriptive_technical_event_history",
            ["completedAtUtc"] = "2026-09-21T13:05:00Z",
            ["contractDefinitionsSha256"] = new string('a', 64),
            ["schema"] = "btc-technical-evidence-run-index/v1",
            ["status"] = "succeeded",
            ["symbol"] = indexSymbolOverride ?? symbol
        };
        var indexBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(indexValue) + "\n");
        var indexHash = HashBytes(indexBytes);
        var indexFile = $"{indexHash}.run-index.json";
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, indexFile), indexBytes);
        _runIndexByDirectory[target] = (indexFile, indexHash);
        var pointer = new SortedDictionary<string, object?>
        {
            ["runIndexFileName"] = indexFile,
            ["runIndexSha256"] = indexHash,
            ["schema"] = "btc-technical-evidence-run-pointer/v1",
            ["updatedAtUtc"] = "2026-09-21T13:05:01Z"
        };
        pointer["pointerSha256"] = HashCanonical(pointer);
        File.WriteAllText(Path.Combine(target, "latest-success.json"), JsonSerializer.Serialize(pointer), new UTF8Encoding(false));
        return ids;
    }

    private string WriteTechnicalDescriptiveBundle(
        string timeframe = "4h",
        bool tamperLedger = false,
        bool predictiveEvidence = false,
        string symbol = "BTCUSDT",
        string? subdirectory = null)
    {
        var target = subdirectory is null
            ? Path.Combine(_root, "technical-descriptive")
            : Path.Combine(_root, "technical-descriptive", subdirectory);
        Directory.CreateDirectory(target);
        var snapshotBytes = Encoding.UTF8.GetBytes("{\"schema\":\"btc-technical-event-descriptive-evidence/v1\"}\n");
        var ledgerBytes = Encoding.UTF8.GetBytes("{\"eventId\":\"one\"}\n{\"eventId\":\"two\"}\n");
        var summary = new SortedDictionary<string, object?>
        {
            ["count"] = 1L,
            ["mean"] = 0.01,
            ["median"] = 0.01,
            ["q25"] = 0.01,
            ["q75"] = 0.01,
            ["meanBlockBootstrapInterval"] = new SortedDictionary<string, object?>
            {
                ["lower"] = 0.005,
                ["upper"] = 0.015
            }
        };
        var comparison = new SortedDictionary<string, object?>
        {
            ["count"] = 1L,
            ["mean"] = 0.002,
            ["median"] = 0.002,
            ["q25"] = 0.002,
            ["q75"] = 0.002,
            ["meanBlockBootstrapInterval"] = new SortedDictionary<string, object?>
            {
                ["lower"] = -0.001,
                ["upper"] = 0.004
            }
        };
        var report = new SortedDictionary<string, object?>
        {
            ["claimType"] = "descriptive_technical_event_history",
            ["counts"] = new SortedDictionary<string, object?>
            {
                ["eligible"] = 1L,
                ["excluded"] = 1L,
                ["exclusionReasons"] = new SortedDictionary<string, object?> { ["unknown_availability"] = 1L },
                ["realizedAtMaxHorizon"] = 1L,
                ["stored"] = 2L
            },
            ["declaredFamily"] = new SortedDictionary<string, object?>
            {
                ["contextKeys"] = new[] { "trend", "volatility" },
                ["eventTypes"] = new[] { "BOS_BULL" },
                ["horizonsBars"] = new[] { 1, 3, 6 },
                ["metrics"] = new[] { "forwardReturn", "mfe", "mae" },
                ["selection"] = "all eligible rows"
            },
            ["dependence"] = new SortedDictionary<string, object?>
            {
                ["dedupBars"] = 6L,
                ["independenceClaimed"] = false,
                ["intervalMethod"] = "moving block bootstrap over chronological event rows",
                ["overlapCandidatesExcluded"] = 0L,
                ["remainingOutcomeWindowsMayOverlap"] = true
            },
            ["economicClaim"] = false,
            ["evidenceProfiles"] = new SortedDictionary<string, object?>
            {
                ["regimeConditioned"] = new SortedDictionary<string, object?>
                {
                    ["filters"] = new[] { "trend=up", "volatility=normal" },
                    ["sampleSize"] = 7L
                },
                ["unconditional"] = new SortedDictionary<string, object?>
                {
                    ["population"] = "all-eligible-events",
                    ["sampleSize"] = 11L
                }
            },
            ["statisticalEvidence"] = new SortedDictionary<string, object?>
            {
                ["eventOrder"] = new[] { "BOS_BULL", "FVG_BEAR" },
                ["familywise"] = new SortedDictionary<string, object?>
                {
                    ["adjustedPValue"] = 0.0125,
                    ["hypotheses"] = 42L
                },
                ["method"] = "moving-block-bootstrap"
            },
            ["sensitivityAudit"] = new SortedDictionary<string, object?>
            {
                ["method"] = "one_axis_at_a_time",
                ["promotionAllowed"] = false,
                ["resultSelection"] = false,
                ["variantCount"] = 1L,
                ["variants"] = new object[]
                {
                    new SortedDictionary<string, object?>
                    {
                        ["module"] = "technicalIndicators",
                        ["variantId"] = "rsi-period-21",
                        ["parameters"] = new SortedDictionary<string, object?> { ["rsiPeriod"] = 21L },
                        ["stored"] = 2L,
                        ["eligible"] = 1L,
                        ["excluded"] = 1L,
                        ["realizedAtMaxHorizon"] = 1L,
                        ["exclusionReasons"] = new SortedDictionary<string, object?> { ["unknown_availability"] = 1L },
                        ["overlapCandidatesExcluded"] = 0L,
                        ["eligibleDecisionTimeJaccardVsBaseline"] = 0.5,
                        ["horizons"] = new SortedDictionary<string, object?>
                        {
                            ["1"] = new SortedDictionary<string, object?>
                            {
                                ["elapsedTimeMs"] = 14_400_000L,
                                ["realized"] = 1L,
                                ["metrics"] = new SortedDictionary<string, object?>
                                {
                                    ["forwardReturn"] = new SortedDictionary<string, object?>
                                    {
                                        ["variant"] = summary,
                                        ["baselineMean"] = 0.008,
                                        ["meanDeltaVsBaseline"] = 0.002
                                    }
                                }
                            }
                        }
                    }
                }
            },
            ["eventTypes"] = new SortedDictionary<string, object?>
            {
                ["BOS_BULL"] = new SortedDictionary<string, object?>
                {
                    ["eligible"] = 1L,
                    ["horizons"] = new SortedDictionary<string, object?>
                    {
                        ["1"] = new SortedDictionary<string, object?>
                        {
                            ["elapsedTimeMs"] = 14_400_000L,
                            ["eligible"] = 1L,
                            ["matchedControls"] = 1L,
                            ["metrics"] = new SortedDictionary<string, object?>
                            {
                                ["forwardReturn"] = new SortedDictionary<string, object?>
                                {
                                    ["event"] = summary,
                                    ["matchedControlDifference"] = comparison
                                }
                            },
                            ["realized"] = 1L
                        }
                    },
                    ["lifecycleCoverage"] = new SortedDictionary<string, object?> { ["supported"] = 0L, ["unavailable"] = 1L },
                    ["stored"] = 1L,
                    ["timeToFirstTouchBars"] = null,
                    ["timeToInvalidationBars"] = null,
                    ["timeToMitigationBars"] = null
                }
            },
            ["limitations"] = new[] { "Historical descriptions are not probabilities." },
            ["modules"] = new SortedDictionary<string, object?>
            {
                ["causalSmc"] = new SortedDictionary<string, object?>
                {
                    ["causalRows"] = 1L,
                    ["excludedLegacyOrUnknownAvailabilityRows"] = 1L,
                    ["reason"] = null,
                    ["status"] = "partial"
                },
                ["finalizedCandles"] = new SortedDictionary<string, object?>
                {
                    ["excludedInvalidDurationRows"] = 2L,
                    ["includedRows"] = 100L,
                    ["status"] = "evaluable"
                }
            },
            ["predictiveEvidence"] = predictiveEvidence,
            ["probabilityClaim"] = false,
            ["promotionAllowed"] = false,
            ["schema"] = "btc-technical-event-descriptive-evidence/v1",
            ["scope"] = new SortedDictionary<string, object?>
            {
                ["cutoffMs"] = 1_789_948_799_999L,
                ["symbol"] = symbol,
                ["timeframe"] = timeframe
            }
        };
        var reportBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(report) + "\n");
        var snapshotHash = HashBytes(snapshotBytes);
        var ledgerHash = HashBytes(ledgerBytes);
        var reportHash = HashBytes(reportBytes);
        File.WriteAllBytes(Path.Combine(target, $"{snapshotHash}.snapshot.json"), snapshotBytes);
        File.WriteAllBytes(Path.Combine(target, $"{ledgerHash}.ledger.jsonl"), ledgerBytes);
        File.WriteAllBytes(Path.Combine(target, $"{reportHash}.report.json"), reportBytes);
        var manifest = new SortedDictionary<string, object?>
        {
            ["artifacts"] = new SortedDictionary<string, object?>
            {
                ["ledger"] = DescriptiveArtifact($"{ledgerHash}.ledger.jsonl", ledgerHash, ledgerBytes.Length),
                ["report"] = DescriptiveArtifact($"{reportHash}.report.json", reportHash, reportBytes.Length),
                ["snapshot"] = DescriptiveArtifact($"{snapshotHash}.snapshot.json", snapshotHash, snapshotBytes.Length)
            },
            ["claimType"] = "descriptive_technical_event_history",
            ["code"] = new SortedDictionary<string, object?>
            {
                ["fileName"] = "technical_event_descriptive_evidence.py",
                ["git"] = new SortedDictionary<string, object?> { ["commit"] = "deadbeef", ["dirty"] = true },
                ["sha256"] = new string('d', 64)
            },
            ["configuration"] = new SortedDictionary<string, object?>
            {
                ["alpha"] = 0.05,
                ["blockSizeEvents"] = 8L,
                ["bootstrapSamples"] = 2000L,
                ["contextKeys"] = new[] { "trend", "volatility" },
                ["dedupBars"] = 6L,
                ["horizonsBars"] = new[] { 1, 3, 6 },
                ["randomSeed"] = 42L
            },
            ["createdFromCutoffMs"] = 1_789_948_799_999L,
            ["immutability"] = "content-addressed",
            ["schema"] = "btc-technical-event-descriptive-evidence/v1",
            ["scope"] = new SortedDictionary<string, object?>
            {
                ["cutoffMs"] = 1_789_948_799_999L,
                ["symbol"] = symbol,
                ["timeframe"] = timeframe
            },
            ["sourceLineage"] = new SortedDictionary<string, object?>
            {
                ["contentSha256"] = new string('c', 64),
                ["source"] = "postgresql:Klines+SmartMoneyStructures",
                ["sourceVersion"] = new string('b', 64)
            }
        };
        var manifestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest) + "\n");
        var id = HashBytes(manifestBytes);
        File.WriteAllBytes(Path.Combine(target, $"{id}.manifest.json"), manifestBytes);
        if (tamperLedger)
            File.AppendAllText(Path.Combine(target, $"{ledgerHash}.ledger.jsonl"), "{\"tampered\":true}\n", new UTF8Encoding(false));
        return id;

        static SortedDictionary<string, object?> DescriptiveArtifact(string fileName, string sha256, int sizeBytes) => new()
        {
            ["fileName"] = fileName,
            ["sha256"] = sha256,
            ["sizeBytes"] = sizeBytes
        };
    }

    private void WritePipelineStatus(
        IReadOnlyList<string> ids,
        bool tamperAfterHash,
        bool stale,
        IReadOnlyList<string>? timeframes = null,
        string? subdirectory = null)
    {
        var target = subdirectory is null
            ? Path.Combine(_root, "technical-descriptive")
            : Path.Combine(_root, "technical-descriptive", subdirectory);
        Directory.CreateDirectory(target);
        timeframes ??= ["1h", "4h", "1d"];
        var runIndex = _runIndexByDirectory[target];
        var rows = new List<SortedDictionary<string, object?>>();
        for (var index = 0; index < timeframes.Count; index++)
        {
            rows.Add(new SortedDictionary<string, object?>
            {
                ["cutoffMs"] = 1_789_948_799_999L,
                ["definitionsSha256"] = new string('a', 64),
                ["eligible"] = 1L,
                ["excluded"] = 1L,
                ["manifestSha256"] = ids[index],
                ["realizedAtMaxHorizon"] = 1L,
                ["semanticVerification"] = true,
                ["stored"] = 2L,
                ["timeframe"] = timeframes[index]
            });
        }
        var status = new SortedDictionary<string, object?>
        {
            ["lastError"] = null,
            ["lastFailedAtUtc"] = null,
            ["lastStartedAtUtc"] = "2026-09-21T13:00:00Z",
            ["lastSucceededAtUtc"] = "2026-09-21T13:05:00Z",
            ["locked"] = false,
            ["running"] = false,
            ["runIndexFileName"] = runIndex.File,
            ["runIndexSha256"] = runIndex.Hash,
            ["schema"] = "btc-technical-evidence-pipeline-status/v1",
            ["staleAfterUtc"] = stale ? "2026-09-21T13:30:00Z" : "2026-09-22T13:05:00Z",
            ["timeframes"] = rows,
            ["updatedAtUtc"] = "2026-09-21T13:05:00Z"
        };
        status["statusSha256"] = HashCanonical(status);
        if (tamperAfterHash)
            status["running"] = true;
        File.WriteAllText(Path.Combine(target, "pipeline-status.json"), JsonSerializer.Serialize(status), new UTF8Encoding(false));
    }

    private static string HashCanonical(object value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = JavaScriptEncoder.Default });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static string HashBytes(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    // Read exactly once per atomic-run verification, so it counts verifications and
    // can hold or fail one mid-flight.
    private sealed class GatedContract : ITechnicalModuleContractProvider
    {
        private readonly ManualResetEventSlim _open = new(true);
        private int _verifications;
        public ManualResetEventSlim Entered { get; } = new(false);
        public int Verifications => Volatile.Read(ref _verifications);
        public volatile Exception? Failure;
        public void Hold()
        {
            Entered.Reset();
            _open.Reset();
        }
        public void Release() => _open.Set();

        public string Sha256
        {
            get
            {
                Interlocked.Increment(ref _verifications);
                Entered.Set();
                _open.Wait(TimeSpan.FromSeconds(30));
                return Failure is { } failure ? throw failure : new string('a', 64);
            }
        }

        public string ContractVersion => "test";
        public string RawFileSha256 => new('b', 64);
        public string CanonicalJson => "{}";
        public string GetCalculationVersion(string moduleKey) => "test";
    }

    private sealed class TestLogger : ILogger<ResearchEvidenceCatalog>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Console.WriteLine(formatter(state, exception));
    }

    private sealed class FakeEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Backend.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
