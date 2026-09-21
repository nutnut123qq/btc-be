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

    private ResearchEvidenceCatalog CreateService() => new(
        new FakeEnvironment { ContentRootPath = _root },
        Microsoft.Extensions.Options.Options.Create(new EvidenceCatalogOptions { RootPath = ".", MaxArtifactBytes = 1_000_000 }),
        new FixedTimeProvider(FixedNow),
        new TestLogger());

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
