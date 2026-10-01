using Backend.Options;
using Backend.Services.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Backend.Services;

public interface IResearchEvidenceCatalog
{
    ResearchEvidenceCatalogResponse GetCatalog();
    ResearchEvidenceDetailDto? GetDetail(string id);
}

/// <summary>
/// Reads the small, immutable research reports produced by the Python evaluators.
/// Only normalized, explicitly allow-listed fields leave this service; source paths,
/// database connection details, raw trial ledgers and per-row timestamps stay private.
/// </summary>
public sealed partial class ResearchEvidenceCatalog : IResearchEvidenceCatalog
{
    private static readonly IReadOnlyDictionary<string, EvidenceKindDefinition> SupportedKinds =
        new Dictionary<string, EvidenceKindDefinition>(StringComparer.Ordinal)
        {
            ["ml-v3"] = new("model", "Reproducible ML evidence bundle"),
            ["ml-v2"] = new("model", "ML walk-forward evidence"),
            ["feature-groups"] = new("feature", "Feature-group ablation"),
            ["technical-events"] = new("event", "Technical-event predictive screen"),
            ["technical-descriptive"] = new("event", "Technical-event descriptive history")
        };

    private readonly string _rootPath;
    private readonly long _maxArtifactBytes;
    private readonly long _maxBundleArtifactBytes;
    private readonly TimeSpan _verificationInterval;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ResearchEvidenceCatalog> _logger;
    private readonly ITechnicalModuleContractProvider? _technicalContract;
    private readonly object _cacheLock = new();
    private string? _cacheFingerprint;
    private LoadResult? _cachedLoad;
    private DateTimeOffset _cacheVerifiedAtUtc;

    public ResearchEvidenceCatalog(
        IHostEnvironment environment,
        IOptions<EvidenceCatalogOptions> options,
        TimeProvider timeProvider,
        ILogger<ResearchEvidenceCatalog> logger,
        ITechnicalModuleContractProvider? technicalContract = null)
    {
        var configured = options.Value.RootPath;
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("EvidenceCatalog:RootPath is required.");

        _rootPath = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
        _maxArtifactBytes = Math.Clamp(options.Value.MaxArtifactBytes, 1_024, 32L * 1024 * 1024);
        _maxBundleArtifactBytes = Math.Clamp(options.Value.MaxBundleArtifactBytes, _maxArtifactBytes, 8L * 1024 * 1024 * 1024);
        _verificationInterval = TimeSpan.FromSeconds(Math.Clamp(options.Value.CacheVerificationSeconds, 0, 86_400));
        _timeProvider = timeProvider;
        _logger = logger;
        _technicalContract = technicalContract;
    }

    public ResearchEvidenceCatalogResponse GetCatalog()
    {
        var load = LoadVerifiedArtifacts();
        var items = load.Artifacts
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenBy(x => x.Kind, StringComparer.Ordinal)
            .Select(x => x.Item)
            .ToArray();
        return ResearchEvidenceCatalogResponse.Create(
            _timeProvider.GetUtcNow().UtcDateTime,
            items,
            new EvidenceIntegritySummaryDto(load.Scanned, items.Length, load.Rejected),
            LoadPipelineStatus(load.Artifacts.Select(x => x.Id).ToHashSet(StringComparer.Ordinal), load.AtomicRun));
    }

    private ResearchEvidencePipelineStatusDto? LoadPipelineStatus(
        IReadOnlySet<string> verifiedManifestIds,
        AtomicTechnicalRun? atomicRun)
    {
        var directory = Path.Combine(_rootPath, "technical-descriptive");
        var statusPath = Path.Combine(directory, "pipeline-status.json");
        if (!File.Exists(statusPath))
            return null;
        try
        {
            var bytes = ReadBounded(statusPath);
            using var document = JsonDocument.Parse(bytes, StrictJsonOptions);
            var status = document.RootElement;
            RequireObject(status, "technical evidence pipeline status");
            if (!string.Equals(RequiredString(status, "schema"), "btc-technical-evidence-pipeline-status/v1", StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported technical evidence pipeline status schema.");
            var declaredHash = RequiredString(status, "statusSha256");
            if (!Sha256Pattern().IsMatch(declaredHash)
                || !FixedTimeEquals(ComputeCanonicalHash(status, "statusSha256"), declaredHash))
                throw new InvalidDataException("Technical evidence pipeline status hash verification failed.");

            var started = OptionalUtcDateTime(status, "lastStartedAtUtc");
            var succeeded = OptionalUtcDateTime(status, "lastSucceededAtUtc");
            var failed = OptionalUtcDateTime(status, "lastFailedAtUtc");
            var updated = OptionalUtcDateTime(status, "updatedAtUtc");
            var staleAfter = OptionalUtcDateTime(status, "staleAfterUtc");
            if (updated is null || staleAfter is null)
                throw new InvalidDataException("Technical evidence pipeline status timestamps are missing.");
            var actuallyLocked = File.Exists(Path.Combine(directory, ".pipeline.lock"));
            if (staleAfter < _timeProvider.GetUtcNow().UtcDateTime)
                return new ResearchEvidencePipelineStatusDto(
                    "stale", true, false, actuallyLocked, started, succeeded, failed,
                    "Pipeline status expired; inspect the scheduler and lock locally.", updated, staleAfter, []);

            var running = OptionalBoolean(status, "running")
                ?? throw new InvalidDataException("Pipeline running state is invalid.");
            var declaredLocked = OptionalBoolean(status, "locked")
                ?? throw new InvalidDataException("Pipeline locked state is invalid.");
            if (running && (!declaredLocked || !actuallyLocked))
                throw new InvalidDataException("Pipeline claims to run without an active lock.");
            var statusIndexFile = OptionalString(status, "runIndexFileName");
            var statusIndexHash = OptionalString(status, "runIndexSha256");

            var timeframes = new List<ResearchEvidencePipelineTimeframeStatusDto>();
            var values = RequiredProperty(status, "timeframes");
            if (values.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Pipeline timeframe status must be an array.");
            foreach (var value in values.EnumerateArray())
            {
                RequireObject(value, "pipeline timeframe");
                var timeframe = RequiredString(value, "timeframe");
                if (timeframe is not ("1h" or "4h" or "1d") || timeframes.Any(x => x.Timeframe == timeframe))
                    throw new InvalidDataException("Pipeline timeframe is unsupported or duplicated.");
                var manifest = RequiredString(value, "manifestSha256");
                var definitions = OptionalString(value, "definitionsSha256");
                if (!Sha256Pattern().IsMatch(manifest) || !verifiedManifestIds.Contains(manifest)
                    || definitions is not null && !Sha256Pattern().IsMatch(definitions))
                    throw new InvalidDataException("Pipeline references an unverified manifest or contract hash.");
                var cutoff = OptionalInt64(value, "cutoffMs");
                var stored = OptionalInt64(value, "stored");
                var eligible = OptionalInt64(value, "eligible");
                var excluded = OptionalInt64(value, "excluded");
                var realized = OptionalInt64(value, "realizedAtMaxHorizon");
                var semantic = OptionalBoolean(value, "semanticVerification");
                if (cutoff is null or <= 0 || stored is null or < 0 || eligible is null or < 0
                    || excluded is null or < 0 || realized is null or < 0 || stored != eligible + excluded
                    || realized > eligible || semantic is not true)
                    throw new InvalidDataException("Pipeline timeframe coverage is inconsistent.");
                timeframes.Add(new ResearchEvidencePipelineTimeframeStatusDto(
                    timeframe, cutoff.Value, manifest, stored.Value, eligible.Value, excluded.Value,
                    realized.Value, definitions, true));
            }
            var lastError = OptionalString(status, "lastError");
            if (lastError?.Length > 240 || lastError?.Contains("://", StringComparison.Ordinal) == true)
                lastError = "Pipeline failed; inspect local service logs.";
            var state = running ? "running"
                : failed is not null && (succeeded is null || failed > succeeded) ? "failed"
                : succeeded is not null ? "succeeded"
                : "idle";
            if (state == "succeeded" && !timeframes.Select(x => x.Timeframe).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(["1h", "4h", "1d"]))
                throw new InvalidDataException("Successful pipeline status does not cover 1h, 4h and 1d.");
            if (state == "succeeded" && (atomicRun is null
                || !string.Equals(statusIndexFile, atomicRun.RunIndexFileName, StringComparison.Ordinal)
                || !string.Equals(statusIndexHash, atomicRun.RunIndexSha256, StringComparison.Ordinal)))
                throw new InvalidDataException("Successful pipeline status does not match the atomic latest-success run.");
            if (state == "succeeded" && atomicRun is not null)
            {
                foreach (var bundle in atomicRun.Bundles)
                {
                    var statusBundle = timeframes.Single(x => x.Timeframe == bundle.Timeframe);
                    if (!string.Equals(statusBundle.ManifestSha256, bundle.ManifestSha256, StringComparison.Ordinal)
                        || statusBundle.CutoffMs != bundle.CutoffMs || statusBundle.Stored != bundle.Stored
                        || statusBundle.Eligible != bundle.Eligible || statusBundle.Excluded != bundle.Excluded
                        || statusBundle.RealizedAtMaxHorizon != bundle.RealizedAtMaxHorizon
                        || !string.Equals(statusBundle.DefinitionsSha256, atomicRun.ContractDefinitionsSha256, StringComparison.Ordinal))
                        throw new InvalidDataException("Pipeline timeframe status does not match the atomic run index.");
                }
            }
            return new ResearchEvidencePipelineStatusDto(
                state, true, running, actuallyLocked, started, succeeded, failed, lastError,
                updated, staleAfter, timeframes.OrderBy(x => x.Timeframe, StringComparer.Ordinal).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or JsonException or CryptographicException or FormatException)
        {
            _logger.LogWarning("Rejected technical evidence pipeline status: {Reason}", ex.Message);
            return new ResearchEvidencePipelineStatusDto(
                "unavailable", false, false, File.Exists(Path.Combine(directory, ".pipeline.lock")),
                null, null, null, "Pipeline status unavailable because integrity validation failed.",
                null, null, []);
        }
    }

    public ResearchEvidenceDetailDto? GetDetail(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !Sha256Pattern().IsMatch(id))
            return null;
        return LoadVerifiedArtifacts().Artifacts
            .SingleOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal))
            ?.Detail;
    }

    private LoadResult LoadVerifiedArtifacts()
    {
        var fingerprint = ComputeArtifactsFingerprint();
        var now = _timeProvider.GetUtcNow();
        lock (_cacheLock)
        {
            if (_cachedLoad is not null
                && string.Equals(fingerprint, _cacheFingerprint, StringComparison.Ordinal)
                && now - _cacheVerifiedAtUtc < _verificationInterval)
                return _cachedLoad;
            var load = LoadVerifiedArtifactsUncached();
            _cachedLoad = load;
            _cacheFingerprint = fingerprint;
            _cacheVerifiedAtUtc = now;
            return load;
        }
    }

    // Cheap identity of the on-disk artifact set: file inventory (name, size,
    // mtime) plus the content hash of the atomic latest-success pointer. Any
    // publish, retention delete or ordinary tamper changes it and forces a full
    // re-verification; mtime-preserving tamper is still caught by the TTL.
    private string ComputeArtifactsFingerprint()
    {
        var builder = new StringBuilder(256);
        foreach (var supported in SupportedKinds.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            var directory = Path.Combine(_rootPath, supported);
            if (!Directory.Exists(directory))
                continue;
            builder.Append(supported).Append('\n');
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var info = new FileInfo(file);
                    builder.Append(info.Name).Append('|').Append(info.Length).Append('|')
                        .Append(info.LastWriteTimeUtc.Ticks).Append('\n');
                }
                if (supported == "technical-descriptive")
                {
                    var pointerPath = Path.Combine(directory, "latest-success.json");
                    builder.Append("pointer|").Append(File.Exists(pointerPath) ? HashFile(pointerPath) : "missing").Append('\n');
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Directory mutating mid-scan (publish/retention race): make the
                // fingerprint unique so the request falls through to the full
                // verification path rather than serving a stale snapshot.
                builder.Append("unstable|").Append(Guid.NewGuid()).Append('\n');
            }
        }
        return HashBytes(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private LoadResult LoadVerifiedArtifactsUncached()
    {
        if (!Directory.Exists(_rootPath))
            return new LoadResult([], 0, 0, null);

        var artifacts = new List<LoadedArtifact>();
        var scanned = 0;
        var rejected = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        AtomicTechnicalRun? atomicRun = null;

        foreach (var supported in SupportedKinds)
        {
            var directory = Path.Combine(_rootPath, supported.Key);
            if (!Directory.Exists(directory))
                continue;

            if (supported.Key == "technical-descriptive")
            {
                if (!File.Exists(Path.Combine(directory, "latest-success.json")))
                    continue;
                scanned += 5;
                try
                {
                    atomicRun = LoadAtomicTechnicalRun(directory, supported.Value);
                    foreach (var artifact in atomicRun.Artifacts)
                    {
                        if (!ids.Add(artifact.Id))
                            throw new InvalidDataException("Duplicate evidence id.");
                        artifacts.Add(artifact);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or InvalidDataException or InvalidOperationException or JsonException or CryptographicException
                    or FormatException)
                {
                    rejected++;
                    atomicRun = null;
                    _logger.LogWarning("Rejected atomic technical evidence run: {Reason}", ex.Message);
                }
                continue;
            }

            if (supported.Key == "ml-v3")
            {
                foreach (var manifestPath in Directory.EnumerateFiles(directory, "*.manifest.json", SearchOption.TopDirectoryOnly))
                {
                    scanned++;
                    try
                    {
                        var fileName = Path.GetFileName(manifestPath);
                        var match = BundleManifestNamePattern().Match(fileName);
                        if (!match.Success)
                            throw new InvalidDataException("Bundle manifest filename is not content-addressed.");
                        var id = match.Groups[1].Value;
                        if (!ids.Add(id))
                            throw new InvalidDataException("Duplicate evidence id.");
                        artifacts.Add(LoadBundleArtifact(manifestPath, id, supported.Key, supported.Value));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                        or InvalidDataException or InvalidOperationException or JsonException or CryptographicException)
                    {
                        rejected++;
                        _logger.LogWarning(
                            "Rejected research evidence bundle {ArtifactName}: {Reason}",
                            Path.GetFileName(manifestPath), ex.Message);
                    }
                }
                continue;
            }

            foreach (var reportPath in Directory.EnumerateFiles(directory, "*.report.json", SearchOption.TopDirectoryOnly))
            {
                scanned++;
                try
                {
                    var fileName = Path.GetFileName(reportPath);
                    var match = ReportNamePattern().Match(fileName);
                    if (!match.Success)
                        throw new InvalidDataException("Report filename is not content-addressed.");
                    var id = match.Groups[1].Value;
                    if (!ids.Add(id))
                        throw new InvalidDataException("Duplicate evidence id.");

                    artifacts.Add(LoadArtifact(reportPath, id, supported.Key, supported.Value));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or InvalidDataException or InvalidOperationException or JsonException or CryptographicException)
                {
                    rejected++;
                    _logger.LogWarning(
                        "Rejected research evidence artifact {ArtifactName}: {Reason}",
                        Path.GetFileName(reportPath), ex.Message);
                }
            }
        }

        return new LoadResult(artifacts, scanned, rejected, atomicRun);
    }

    private LoadedArtifact LoadBundleArtifact(
        string manifestPath,
        string id,
        string directoryName,
        EvidenceKindDefinition definition)
    {
        var manifestBytes = ReadBounded(manifestPath);
        using var bundleDocument = JsonDocument.Parse(manifestBytes, StrictJsonOptions);
        var bundle = bundleDocument.RootElement;
        RequireObject(bundle, "bundle manifest");
        if (!string.Equals(RequiredString(bundle, "schemaVersion"), "btc-ml-evidence-bundle/v3", StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported evidence bundle schema.");
        var declaredBundleHash = RequiredString(bundle, "bundleManifestSha256");
        if (!string.Equals(declaredBundleHash, id, StringComparison.Ordinal)
            || !FixedTimeEquals(ComputeCanonicalHash(bundle, "bundleManifestSha256"), id))
            throw new InvalidDataException("Bundle manifest hash verification failed.");

        var researchManifestHash = RequiredString(bundle, "researchManifestSha256");
        if (!Sha256Pattern().IsMatch(researchManifestHash))
            throw new InvalidDataException("Bundle research manifest hash is invalid.");
        var declarations = RequiredProperty(bundle, "artifacts");
        RequireObject(declarations, "bundle artifacts");
        string[] requiredRoles = ["datasetSnapshot", "rowPredictions", "report"];
        if (!declarations.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal)
                .SetEquals(requiredRoles))
            throw new InvalidDataException("Bundle artifact roles do not match the v3 schema.");
        var directory = Path.GetDirectoryName(manifestPath)!;
        var verifiedArtifacts = new List<ResearchEvidenceArtifactDto>();
        string? reportPath = null;
        string? reportFileHash = null;

        foreach (var role in requiredRoles)
        {
            var declaration = RequiredProperty(declarations, role);
            RequireObject(declaration, role);
            var file = RequiredString(declaration, "file");
            if (!string.Equals(file, Path.GetFileName(file), StringComparison.Ordinal)
                || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException($"Bundle artifact '{role}' has an unsafe filename.");
            var expectedHash = RequiredString(declaration, "sha256");
            if (!Sha256Pattern().IsMatch(expectedHash))
                throw new InvalidDataException($"Bundle artifact '{role}' has an invalid hash.");
            var expectedSuffix = role switch
            {
                "datasetSnapshot" => "dataset.npz",
                "rowPredictions" => "predictions.jsonl",
                "report" => "report.json",
                _ => throw new InvalidDataException("Unsupported bundle artifact role.")
            };
            if (!string.Equals(file, $"{expectedHash}.{expectedSuffix}", StringComparison.Ordinal))
                throw new InvalidDataException($"Bundle artifact '{role}' filename is not content-addressed.");
            var expectedBytes = OptionalInt64(declaration, "bytes");
            if (expectedBytes is null || expectedBytes <= 0 || expectedBytes > _maxBundleArtifactBytes)
                throw new InvalidDataException($"Bundle artifact '{role}' has an invalid size.");

            var artifactPath = Path.Combine(directory, file);
            var info = new FileInfo(artifactPath);
            if (!info.Exists || info.Length != expectedBytes)
                throw new InvalidDataException($"Bundle artifact '{role}' size verification failed.");
            var actualHash = HashFile(artifactPath);
            if (!FixedTimeEquals(actualHash, expectedHash))
                throw new InvalidDataException($"Bundle artifact '{role}' hash verification failed.");
            verifiedArtifacts.Add(new ResearchEvidenceArtifactDto(role, actualHash, info.Length, null));
            if (role == "report")
            {
                reportPath = artifactPath;
                reportFileHash = actualHash;
            }
        }

        if (reportPath is null || reportFileHash is null)
            throw new InvalidDataException("Bundle report is missing.");
        var reportBytes = ReadBounded(reportPath);
        if (!FixedTimeEquals(HashBytes(reportBytes), reportFileHash))
            throw new InvalidDataException("Bundle report changed during verification.");
        using var reportDocument = JsonDocument.Parse(reportBytes, StrictJsonOptions);
        var report = reportDocument.RootElement;
        RequireObject(report, "bundle report");
        if (!string.Equals(OptionalString(report, "schemaVersion"), "btc-ml-evidence-bundle/v3", StringComparison.Ordinal))
            throw new InvalidDataException("Bundle report schema does not match the manifest.");
        var reportPayloadHash = RequiredString(report, "reportPayloadSha256");
        if (!Sha256Pattern().IsMatch(reportPayloadHash)
            || !FixedTimeEquals(ComputeCanonicalHash(report, "reportPayloadSha256"), reportPayloadHash))
            throw new InvalidDataException("Bundle report payload hash verification failed.");

        var researchManifest = RequiredProperty(report, "researchManifest");
        RequireObject(researchManifest, "research manifest");
        var embeddedResearchHash = RequiredString(researchManifest, "manifestSha256");
        if (!string.Equals(embeddedResearchHash, researchManifestHash, StringComparison.Ordinal)
            || !FixedTimeEquals(ComputeCanonicalHash(researchManifest, "manifestSha256"), researchManifestHash))
            throw new InvalidDataException("Bundle research manifest verification failed.");
        if (!string.Equals(RequiredString(researchManifest, "symbol"), "BTCUSDT", StringComparison.Ordinal)
            || !string.Equals(RequiredString(researchManifest, "timeframe"), "4h", StringComparison.Ordinal))
            throw new InvalidDataException("Evidence bundle is outside the BTCUSDT 4h research scope.");

        var evaluation = RequiredProperty(report, "evaluation");
        RequireObject(evaluation, "evaluation");
        if (!string.Equals(OptionalString(evaluation, "schemaVersion"), "btc-ml-evidence-bundle/v3", StringComparison.Ordinal)
            || !string.Equals(OptionalString(evaluation, "evidenceTier"), "retrospective_selection_aware", StringComparison.Ordinal))
            throw new InvalidDataException("Bundle evaluation tier or schema is invalid.");
        var promotionGate = RequiredProperty(evaluation, "promotionGate");
        RequireObject(promotionGate, "promotion gate");
        if (OptionalBoolean(promotionGate, "confirmatory") is not false
            || OptionalBoolean(promotionGate, "promotionAllowed") is not false)
            throw new InvalidDataException("Retrospective v3 evidence must not claim confirmation or promotion eligibility.");
        var datasetRows = manifestRowCount(researchManifest);
        var predictionRows = OptionalInt64(evaluation, "evaluationRows");
        verifiedArtifacts = verifiedArtifacts.Select(x => x with
        {
            RowCount = x.Role switch
            {
                "datasetSnapshot" => datasetRows,
                "rowPredictions" => predictionRows,
                _ => null
            }
        }).ToList();
        var integrity = new EvidenceIntegrityDto(
            Verified: true,
            ManifestHashVerified: true,
            ReportHashVerified: true,
            ReportHashEmbedded: true,
            VerificationMode: "bundle-manifest-all-artifacts-and-report-payload");
        var createdAtUtc = File.GetLastWriteTimeUtc(manifestPath);
        return Normalize(
            id, directoryName, definition, researchManifest, evaluation, reportFileHash,
            createdAtUtc == DateTime.MinValue ? null : DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc),
            integrity,
            verifiedArtifacts);

        static long? manifestRowCount(JsonElement value) =>
            value.TryGetProperty("dataProvenance", out var data) ? OptionalInt64(data, "rowCount") : null;
    }

    private AtomicTechnicalRun LoadAtomicTechnicalRun(
        string directory,
        EvidenceKindDefinition definition)
    {
        var pointerPath = Path.Combine(directory, "latest-success.json");
        var pointerBytes = ReadBounded(pointerPath);
        using var pointerDocument = JsonDocument.Parse(pointerBytes, StrictJsonOptions);
        var pointer = pointerDocument.RootElement;
        RequireObject(pointer, "technical run pointer");
        if (!string.Equals(RequiredString(pointer, "schema"), "btc-technical-evidence-run-pointer/v1", StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported technical run pointer schema.");
        var pointerHash = RequiredString(pointer, "pointerSha256");
        if (!Sha256Pattern().IsMatch(pointerHash)
            || !FixedTimeEquals(ComputeCanonicalHash(pointer, "pointerSha256"), pointerHash))
            throw new InvalidDataException("Technical run pointer hash verification failed.");
        _ = OptionalUtcDateTime(pointer, "updatedAtUtc")
            ?? throw new InvalidDataException("Technical run pointer timestamp is missing.");
        var indexHash = RequiredString(pointer, "runIndexSha256");
        var indexFile = RequiredString(pointer, "runIndexFileName");
        if (!Sha256Pattern().IsMatch(indexHash)
            || !string.Equals(indexFile, $"{indexHash}.run-index.json", StringComparison.Ordinal)
            || !string.Equals(indexFile, Path.GetFileName(indexFile), StringComparison.Ordinal))
            throw new InvalidDataException("Technical run index reference is unsafe or not content-addressed.");

        var indexPath = Path.Combine(directory, indexFile);
        var indexBytes = ReadBounded(indexPath);
        if (!FixedTimeEquals(HashBytes(indexBytes), indexHash))
            throw new InvalidDataException("Technical run index byte hash verification failed.");
        using var indexDocument = JsonDocument.Parse(indexBytes, StrictJsonOptions);
        var index = indexDocument.RootElement;
        RequireObject(index, "technical run index");
        if (!string.Equals(RequiredString(index, "schema"), "btc-technical-evidence-run-index/v1", StringComparison.Ordinal)
            || !string.Equals(RequiredString(index, "claimType"), "descriptive_technical_event_history", StringComparison.Ordinal)
            || !string.Equals(RequiredString(index, "symbol"), "BTCUSDT", StringComparison.Ordinal)
            || !string.Equals(RequiredString(index, "status"), "succeeded", StringComparison.Ordinal))
            throw new InvalidDataException("Technical run index scope, status or claim type is invalid.");
        _ = OptionalUtcDateTime(index, "completedAtUtc")
            ?? throw new InvalidDataException("Technical run completion timestamp is missing.");
        var definitions = RequiredString(index, "contractDefinitionsSha256");
        if (!Sha256Pattern().IsMatch(definitions)
            || _technicalContract is not null
                && !FixedTimeEquals(definitions, _technicalContract.Sha256))
            throw new InvalidDataException("Technical run contract definitions hash is invalid or stale.");

        var bundlesElement = RequiredProperty(index, "bundles");
        if (bundlesElement.ValueKind != JsonValueKind.Array || bundlesElement.GetArrayLength() != 3)
            throw new InvalidDataException("Technical run index must reference exactly three bundles.");
        string[] expectedTimeframes = ["1h", "4h", "1d"];
        var references = new List<AtomicBundleReference>(3);
        for (var i = 0; i < expectedTimeframes.Length; i++)
        {
            var bundle = bundlesElement[i];
            RequireObject(bundle, "technical run bundle reference");
            var timeframe = RequiredString(bundle, "timeframe");
            var manifestHash = RequiredString(bundle, "manifestSha256");
            var manifestFile = RequiredString(bundle, "manifestFileName");
            var cutoff = OptionalInt64(bundle, "cutoffMs");
            var stored = OptionalInt64(bundle, "stored");
            var eligible = OptionalInt64(bundle, "eligible");
            var excluded = OptionalInt64(bundle, "excluded");
            var realized = OptionalInt64(bundle, "realizedAtMaxHorizon");
            var semantic = OptionalBoolean(bundle, "semanticVerification");
            if (!string.Equals(timeframe, expectedTimeframes[i], StringComparison.Ordinal)
                || !Sha256Pattern().IsMatch(manifestHash)
                || !string.Equals(manifestFile, $"{manifestHash}.manifest.json", StringComparison.Ordinal)
                || !string.Equals(manifestFile, Path.GetFileName(manifestFile), StringComparison.Ordinal)
                || cutoff is null or <= 0 || stored is null or < 0 || eligible is null or < 0
                || excluded is null or < 0 || realized is null or < 0
                || stored != eligible + excluded || realized > eligible || semantic is not true)
                throw new InvalidDataException("Technical run bundle reference is inconsistent.");
            references.Add(new AtomicBundleReference(timeframe, cutoff.Value, manifestFile, manifestHash,
                stored.Value, eligible.Value, excluded.Value, realized.Value));
        }

        var artifacts = references.Select(reference => LoadDescriptiveBundleArtifact(
            Path.Combine(directory, reference.ManifestFileName), reference.ManifestSha256, definition, reference)).ToArray();
        return new AtomicTechnicalRun(indexFile, indexHash, definitions, references, artifacts);
    }

    private LoadedArtifact LoadDescriptiveBundleArtifact(
        string manifestPath,
        string id,
        EvidenceKindDefinition definition,
        AtomicBundleReference? expected = null)
    {
        var manifestBytes = ReadBounded(manifestPath);
        if (!FixedTimeEquals(HashBytes(manifestBytes), id))
            throw new InvalidDataException("Descriptive bundle manifest filename hash mismatch.");

        using var manifestDocument = JsonDocument.Parse(manifestBytes, StrictJsonOptions);
        var manifest = manifestDocument.RootElement;
        RequireObject(manifest, "descriptive bundle manifest");
        if (!string.Equals(RequiredString(manifest, "schema"), "btc-technical-event-descriptive-evidence/v1", StringComparison.Ordinal)
            || !string.Equals(RequiredString(manifest, "claimType"), "descriptive_technical_event_history", StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported technical descriptive bundle schema or claim type.");

        var scope = RequiredProperty(manifest, "scope");
        RequireObject(scope, "descriptive bundle scope");
        var symbol = RequiredString(scope, "symbol");
        var timeframe = RequiredString(scope, "timeframe");
        var cutoffMs = OptionalInt64(scope, "cutoffMs");
        if (!string.Equals(symbol, "BTCUSDT", StringComparison.Ordinal)
            || timeframe is not ("1h" or "4h" or "1d")
            || cutoffMs is null
            || cutoffMs != OptionalInt64(manifest, "createdFromCutoffMs"))
            throw new InvalidDataException("Descriptive evidence is outside the BTCUSDT 1h/4h/1d cutoff scope.");
        if (expected is not null && (!string.Equals(expected.Timeframe, timeframe, StringComparison.Ordinal)
            || expected.CutoffMs != cutoffMs || !string.Equals(expected.ManifestSha256, id, StringComparison.Ordinal)))
            throw new InvalidDataException("Atomic run index scope does not match the descriptive manifest.");

        var sourceLineage = RequiredProperty(manifest, "sourceLineage");
        RequireObject(sourceLineage, "source lineage");
        if (SafeDescriptor(RequiredString(sourceLineage, "source")) is null
            || SafeDescriptor(RequiredString(sourceLineage, "sourceVersion")) is null
            || !Sha256Pattern().IsMatch(RequiredString(sourceLineage, "contentSha256")))
            throw new InvalidDataException("Descriptive source lineage is invalid.");

        var declarations = RequiredProperty(manifest, "artifacts");
        RequireObject(declarations, "descriptive bundle artifacts");
        string[] requiredRoles = ["snapshot", "ledger", "report"];
        if (!declarations.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal)
                .SetEquals(requiredRoles))
            throw new InvalidDataException("Descriptive bundle artifact roles do not match the schema.");

        var directory = Path.GetDirectoryName(manifestPath)!;
        var verified = new List<ResearchEvidenceArtifactDto>();
        string? reportPath = null;
        string? ledgerPath = null;
        string? reportHash = null;
        foreach (var role in requiredRoles)
        {
            var declaration = RequiredProperty(declarations, role);
            RequireObject(declaration, $"descriptive artifact {role}");
            var file = RequiredString(declaration, "fileName");
            if (!string.Equals(file, Path.GetFileName(file), StringComparison.Ordinal)
                || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException($"Descriptive artifact '{role}' has an unsafe filename.");
            var expectedHash = RequiredString(declaration, "sha256");
            if (!Sha256Pattern().IsMatch(expectedHash))
                throw new InvalidDataException($"Descriptive artifact '{role}' has an invalid hash.");
            var expectedSuffix = role switch
            {
                "snapshot" => "snapshot.json",
                "ledger" => "ledger.jsonl",
                "report" => "report.json",
                _ => throw new InvalidDataException("Unsupported descriptive artifact role.")
            };
            if (!string.Equals(file, $"{expectedHash}.{expectedSuffix}", StringComparison.Ordinal))
                throw new InvalidDataException($"Descriptive artifact '{role}' filename is not content-addressed.");
            var expectedBytes = OptionalInt64(declaration, "sizeBytes");
            var maxBytes = role == "report" ? _maxArtifactBytes : _maxBundleArtifactBytes;
            if (expectedBytes is null || expectedBytes < 0 || expectedBytes > maxBytes
                || role != "ledger" && expectedBytes == 0)
                throw new InvalidDataException($"Descriptive artifact '{role}' has an invalid size.");
            var artifactPath = Path.Combine(directory, file);
            var info = new FileInfo(artifactPath);
            if (!info.Exists || info.Length != expectedBytes)
                throw new InvalidDataException($"Descriptive artifact '{role}' size verification failed.");
            var actualHash = HashFile(artifactPath);
            if (!FixedTimeEquals(actualHash, expectedHash))
                throw new InvalidDataException($"Descriptive artifact '{role}' hash verification failed.");
            verified.Add(new ResearchEvidenceArtifactDto(role, actualHash, info.Length, null));
            if (role == "report")
            {
                reportPath = artifactPath;
                reportHash = actualHash;
            }
            else if (role == "ledger")
            {
                ledgerPath = artifactPath;
            }
        }

        if (reportPath is null || ledgerPath is null || reportHash is null)
            throw new InvalidDataException("Descriptive bundle is incomplete.");
        var reportBytes = ReadBounded(reportPath);
        if (!FixedTimeEquals(HashBytes(reportBytes), reportHash))
            throw new InvalidDataException("Descriptive report changed during verification.");
        using var reportDocument = JsonDocument.Parse(reportBytes, StrictJsonOptions);
        var report = reportDocument.RootElement;
        RequireObject(report, "descriptive report");
        if (!string.Equals(RequiredString(report, "schema"), "btc-technical-event-descriptive-evidence/v1", StringComparison.Ordinal)
            || !string.Equals(RequiredString(report, "claimType"), "descriptive_technical_event_history", StringComparison.Ordinal)
            || OptionalBoolean(report, "predictiveEvidence") is not false
            || OptionalBoolean(report, "probabilityClaim") is not false
            || OptionalBoolean(report, "economicClaim") is not false
            || OptionalBoolean(report, "promotionAllowed") is not false)
            throw new InvalidDataException("Descriptive report attempts an unsupported claim.");
        var reportScope = RequiredProperty(report, "scope");
        if (!string.Equals(OptionalString(reportScope, "symbol"), symbol, StringComparison.Ordinal)
            || !string.Equals(OptionalString(reportScope, "timeframe"), timeframe, StringComparison.Ordinal)
            || OptionalInt64(reportScope, "cutoffMs") != cutoffMs)
            throw new InvalidDataException("Descriptive report scope does not match its manifest.");

        var counts = RequiredProperty(report, "counts");
        RequireObject(counts, "descriptive counts");
        var stored = OptionalInt64(counts, "stored");
        var eligible = OptionalInt64(counts, "eligible");
        var excluded = OptionalInt64(counts, "excluded");
        var realized = OptionalInt64(counts, "realizedAtMaxHorizon");
        if (stored is null or < 0 || eligible is null or < 0 || excluded is null or < 0
            || realized is null or < 0 || eligible + excluded != stored || realized > eligible)
            throw new InvalidDataException("Descriptive report counts are inconsistent.");
        if (expected is not null && (expected.Stored != stored || expected.Eligible != eligible
            || expected.Excluded != excluded || expected.RealizedAtMaxHorizon != realized))
            throw new InvalidDataException("Atomic run index counts do not match the descriptive report.");
        if (CountJsonLines(ledgerPath) != stored)
            throw new InvalidDataException("Descriptive ledger row count does not match the report.");

        var candleRows = report.TryGetProperty("modules", out var modules)
            && modules.TryGetProperty("finalizedCandles", out var candleModule)
            ? OptionalInt64(candleModule, "includedRows")
            : null;
        verified = verified.Select(x => x with
        {
            RowCount = x.Role switch
            {
                "snapshot" => candleRows,
                "ledger" => stored,
                _ => null
            }
        }).ToList();

        var integrity = new EvidenceIntegrityDto(
            Verified: true,
            ManifestHashVerified: true,
            ReportHashVerified: true,
            ReportHashEmbedded: false,
            VerificationMode: "content-addressed-manifest-all-artifacts-and-ledger-count");
        var createdAtUtc = File.GetLastWriteTimeUtc(manifestPath);
        return NormalizeDescriptiveBundle(
            id,
            definition,
            manifest,
            report,
            symbol,
            timeframe,
            reportHash,
            createdAtUtc == DateTime.MinValue ? null : DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc),
            stored.Value,
            eligible.Value,
            realized.Value,
            candleRows,
            integrity,
            verified);
    }

    private static long CountJsonLines(string path)
    {
        long count = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                throw new InvalidDataException("Descriptive ledger contains a blank row.");
            using var row = JsonDocument.Parse(line, StrictJsonOptions);
            RequireObject(row.RootElement, "descriptive ledger row");
            count++;
        }
        return count;
    }

    private LoadedArtifact LoadArtifact(
        string reportPath,
        string id,
        string directoryName,
        EvidenceKindDefinition definition)
    {
        var reportBytes = ReadBounded(reportPath);
        using var reportDocument = JsonDocument.Parse(reportBytes, StrictJsonOptions);
        var report = reportDocument.RootElement;
        RequireObject(report, "report");

        JsonDocument? separateManifestDocument = null;
        try
        {
            JsonElement manifest;
            if (report.TryGetProperty("manifest", out var embeddedManifest))
            {
                manifest = embeddedManifest;
            }
            else
            {
                var declaredManifestHash = RequiredString(report, "manifestSha256");
                if (!string.Equals(declaredManifestHash, id, StringComparison.Ordinal))
                    throw new InvalidDataException("Report-to-manifest binding does not match its filename.");
                var manifestPath = Path.Combine(Path.GetDirectoryName(reportPath)!, $"{id}.manifest.json");
                var manifestBytes = ReadBounded(manifestPath);
                separateManifestDocument = JsonDocument.Parse(manifestBytes, StrictJsonOptions);
                manifest = separateManifestDocument.RootElement;
            }

            RequireObject(manifest, "manifest");
            var manifestHash = RequiredString(manifest, "manifestSha256");
            if (!string.Equals(manifestHash, id, StringComparison.Ordinal)
                || !FixedTimeEquals(ComputeCanonicalHash(manifest, "manifestSha256"), id))
                throw new InvalidDataException("Manifest hash verification failed.");

            var symbol = RequiredString(manifest, "symbol");
            var timeframe = RequiredString(manifest, "timeframe");
            if (!string.Equals(symbol, "BTCUSDT", StringComparison.Ordinal)
                || !string.Equals(timeframe, "4h", StringComparison.Ordinal))
                throw new InvalidDataException("Evidence is outside the BTCUSDT 4h research scope.");

            var evaluation = RequiredProperty(report, "evaluation");
            RequireObject(evaluation, "evaluation");
            var actualReportHash = Convert.ToHexString(SHA256.HashData(reportBytes)).ToLowerInvariant();
            var reportHashEmbedded = report.TryGetProperty("reportSha256", out var reportHashElement);
            var reportHashVerified = false;
            if (reportHashEmbedded)
            {
                var declaredReportHash = reportHashElement.GetString();
                if (declaredReportHash is null || !Sha256Pattern().IsMatch(declaredReportHash)
                    || !FixedTimeEquals(ComputeCanonicalHash(report, "reportSha256"), declaredReportHash))
                    throw new InvalidDataException("Embedded report hash verification failed.");
                reportHashVerified = true;
            }

            var integrity = new EvidenceIntegrityDto(
                reportHashVerified,
                ManifestHashVerified: true,
                ReportHashVerified: reportHashVerified,
                ReportHashEmbedded: reportHashEmbedded,
                VerificationMode: reportHashEmbedded ? "manifest-and-embedded-report-hash" : "manifest-bound-report-computed-hash-only");

            var createdAtUtc = File.GetLastWriteTimeUtc(reportPath);
            var normalized = Normalize(
                id, directoryName, definition, manifest, evaluation, actualReportHash,
                createdAtUtc == DateTime.MinValue ? null : DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc), integrity,
                [new ResearchEvidenceArtifactDto("report", actualReportHash, reportBytes.LongLength, null)]);
            return normalized;
        }
        finally
        {
            separateManifestDocument?.Dispose();
        }
    }

    private LoadedArtifact Normalize(
        string id,
        string directoryName,
        EvidenceKindDefinition definition,
        JsonElement manifest,
        JsonElement evaluation,
        string reportSha256,
        DateTime? createdAtUtc,
        EvidenceIntegrityDto integrity,
        IReadOnlyList<ResearchEvidenceArtifactDto> artifacts)
    {
        var limitations = LimitationsFor(directoryName).ToList();
        if (!integrity.ReportHashEmbedded)
            limitations.Add("This evaluator did not embed a report hash. The API verifies the content-addressed manifest binding and publishes the computed report SHA-256, but cannot independently authenticate report bytes.");

        var status = StatusFor(directoryName, evaluation, integrity);
        var summary = SummaryFor(directoryName, evaluation);
        var evidenceTier = directoryName == "ml-v3" ? "retrospective-selection-aware" : "predictive";
        var item = new ResearchEvidenceCatalogItemDto(
            id, definition.Kind, definition.Title, status, evidenceTier, "BTCUSDT", "4h",
            createdAtUtc, id, reportSha256, summary, limitations, integrity);

        var detail = new ResearchEvidenceDetailDto(
            id, definition.Kind, definition.Title, status, evidenceTier, "BTCUSDT", "4h",
            createdAtUtc, id, reportSha256, summary,
            HypothesisFor(directoryName),
            DatasetFrom(manifest),
            ProtocolFrom(manifest, evaluation),
            BaselinesFrom(directoryName, evaluation),
            MetricsFrom(directoryName, evaluation),
            FindingsFrom(directoryName, evaluation),
            UncertaintyFrom(directoryName, evaluation),
            CoverageFrom(directoryName, evaluation),
            ConclusionFor(directoryName, evaluation, integrity),
            limitations,
            ProvenanceFrom(manifest),
            artifacts,
            integrity);
        return new LoadedArtifact(id, definition.Kind, createdAtUtc, item, detail);
    }

    private static LoadedArtifact NormalizeDescriptiveBundle(
        string id,
        EvidenceKindDefinition definition,
        JsonElement manifest,
        JsonElement report,
        string symbol,
        string timeframe,
        string reportSha256,
        DateTime? createdAtUtc,
        long stored,
        long eligible,
        long realized,
        long? candleRows,
        EvidenceIntegrityDto integrity,
        IReadOnlyList<ResearchEvidenceArtifactDto> artifacts)
    {
        var sourceLineage = RequiredProperty(manifest, "sourceLineage");
        var source = SafeDescriptor(OptionalString(sourceLineage, "source")) ?? "versioned immutable research input";
        var datasetHash = OptionalString(sourceLineage, "contentSha256");
        var summary = $"Descriptive history for {eligible:N0} causally eligible technical events from {stored:N0} stored BTCUSDT {timeframe} rows; {realized:N0} have a realized six-bar horizon.";
        var limitations = new List<string>
        {
            "Historical event distributions are not probabilities or predictions for a current event.",
            "Outcome windows may overlap after deterministic same-type deduplication; independence is not claimed.",
            "Context-matched controls are historical comparisons, not randomized counterfactuals.",
            "Fees, fills, slippage, position sizing, trades and PnL are outside this artifact."
        };
        if (report.TryGetProperty("modules", out var modules)
            && modules.TryGetProperty("causalSmc", out var smcModule))
        {
            var moduleStatus = OptionalString(smcModule, "status");
            var legacyRows = OptionalInt64(smcModule, "excludedLegacyOrUnknownAvailabilityRows") ?? 0;
            if (moduleStatus is "partial" or "unavailable")
                limitations.Add($"Causal SMC coverage is {moduleStatus}; {legacyRows:N0} legacy or unknown-availability rows were excluded.");
        }
        if (report.TryGetProperty("modules", out var dataModules)
            && dataModules.TryGetProperty("finalizedCandles", out var candleModule))
        {
            var malformed = OptionalInt64(candleModule, "excludedInvalidDurationRows") ?? 0;
            if (malformed > 0)
                limitations.Add($"{malformed:N0} malformed-duration candle rows were excluded before context and outcome calculations.");
        }

        var item = new ResearchEvidenceCatalogItemDto(
            id, definition.Kind, definition.Title, ResearchEvidenceStatuses.Inconclusive, "descriptive",
            symbol, timeframe, createdAtUtc, id, reportSha256, summary, limitations, integrity);

        var code = RequiredProperty(manifest, "code");
        var git = code.TryGetProperty("git", out var gitValue) ? gitValue : default;
        var configuration = RequiredProperty(manifest, "configuration");
        var dependence = RequiredProperty(report, "dependence");
        var alpha = OptionalDouble(configuration, "alpha");
        JsonElement? evidenceProfiles = report.TryGetProperty("evidenceProfiles", out var profileValue)
            ? profileValue.Clone()
            : null;
        JsonElement? statisticalEvidence = report.TryGetProperty("statisticalEvidence", out var statisticalValue)
            ? statisticalValue.Clone()
            : null;
        var detail = new ResearchEvidenceDetailDto(
            id,
            definition.Kind,
            definition.Title,
            ResearchEvidenceStatuses.Inconclusive,
            "descriptive",
            symbol,
            timeframe,
            createdAtUtc,
            id,
            reportSha256,
            summary,
            "Describe the historical post-event distribution and lifecycle of every causally eligible technical event without estimating a current probability or trading outcome.",
            new ResearchEvidenceDatasetDto(source, candleRows, null, OptionalInt64(manifest, "createdFromCutoffMs"), datasetHash),
            new ResearchEvidenceProtocolDto(
                RequiredString(manifest, "schema"),
                "after explicit event availability on a finalized candle",
                "close-to-close return with intrabar high/low excursions",
                true,
                $"No result-based selection; matched controls require controlIndex + 6 < eventDecisionIndex; {OptionalString(dependence, "intervalMethod") ?? "moving block bootstrap"}"),
            [new ResearchEvidenceBaselineDto(
                "prior_context_match",
                "Most recent unused earlier finalized candle with the same declared causal context whose complete six-bar maximum-horizon outcome ended strictly before the event decision; descriptive comparison only.")],
            DescriptiveMetrics(report),
            DescriptiveFindings(report),
            DescriptiveUncertainty(report, alpha is { } a ? 1d - a : null),
            new ResearchEvidenceCoverageDto(
                realized,
                eligible,
                stored > 0 ? (double)eligible / stored : null,
                null),
            "This artifact describes historical event behavior and lifecycle only. It establishes no predictive probability, economic value, promotion decision or trading claim.",
            limitations,
            new ResearchEvidenceProvenanceDto(
                RequiredString(manifest, "schema"),
                "technical-event-descriptive-history",
                OptionalString(code, "sha256"),
                null,
                git.ValueKind == JsonValueKind.Object ? SafeDescriptor(OptionalString(git, "commit")) : null,
                git.ValueKind == JsonValueKind.Object ? OptionalBoolean(git, "dirty") : null),
            artifacts,
            integrity,
            evidenceProfiles,
            statisticalEvidence);
        return new LoadedArtifact(id, definition.Kind, createdAtUtc, item, detail);
    }

    private static IReadOnlyList<ResearchEvidenceMetricDto> DescriptiveMetrics(JsonElement report)
    {
        var result = new List<ResearchEvidenceMetricDto>();
        var counts = RequiredProperty(report, "counts");
        AddMetric(result, "events.stored", "Stored event rows", OptionalDouble(counts, "stored"), "count", null);
        AddMetric(result, "events.eligible", "Causally eligible events", OptionalDouble(counts, "eligible"), "count", null);
        AddMetric(result, "events.excluded", "Excluded event rows", OptionalDouble(counts, "excluded"), "count", null);
        AddMetric(result, "events.realized6Bars", "Events realized at six bars", OptionalDouble(counts, "realizedAtMaxHorizon"), "count", null);
        if (report.TryGetProperty("dependence", out var dependence))
            AddMetric(result, "events.overlapExcluded", "Overlap candidates deduplicated",
                OptionalDouble(dependence, "overlapCandidatesExcluded"), "count", null);
        if (report.TryGetProperty("eventTypes", out var eventTypes) && eventTypes.ValueKind == JsonValueKind.Object)
        {
            foreach (var eventType in eventTypes.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (!EvidenceNamePattern().IsMatch(eventType.Name))
                    continue;
                AddMetric(result, $"event.{eventType.Name}.eligible", $"{eventType.Name} eligible events",
                    OptionalDouble(eventType.Value, "eligible"), "count", null);
            }
        }
        return result;
    }

    private static IReadOnlyList<ResearchEvidenceFindingDto> DescriptiveFindings(JsonElement report)
    {
        var result = new List<ResearchEvidenceFindingDto>();
        if (!report.TryGetProperty("eventTypes", out var eventTypes) || eventTypes.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var eventType in eventTypes.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            if (!EvidenceNamePattern().IsMatch(eventType.Name)
                || !eventType.Value.TryGetProperty("horizons", out var horizons)
                || horizons.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var horizon in horizons.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (!long.TryParse(horizon.Name, out var bars)
                    || !horizon.Value.TryGetProperty("metrics", out var metrics)
                    || metrics.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var metric in metrics.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (!EvidenceNamePattern().IsMatch(metric.Name))
                        continue;
                    AddDescriptiveFinding(result, eventType.Name, bars, metric.Name, "event", metric.Value, "descriptive");
                    AddDescriptiveFinding(result, eventType.Name, bars, metric.Name, "matchedControlDifference", metric.Value, "descriptive-comparison");
                }
            }
        }
        return result;
    }

    private static void AddDescriptiveFinding(
        List<ResearchEvidenceFindingDto> result,
        string eventType,
        long horizonBars,
        string metricName,
        string comparison,
        JsonElement metric,
        string status)
    {
        if (!metric.TryGetProperty(comparison, out var summary) || summary.ValueKind != JsonValueKind.Object)
            return;
        var interval = OptionalObjectInterval(summary, "meanBlockBootstrapInterval");
        result.Add(new ResearchEvidenceFindingDto(
            $"{eventType}:{horizonBars}:{metricName}:{comparison}",
            comparison == "event"
                ? $"{eventType} {metricName} after {horizonBars} bars"
                : $"{eventType} {metricName} minus matched context after {horizonBars} bars",
            status,
            metricName,
            OptionalDouble(summary, "mean"),
            interval?.Lower,
            interval?.Upper,
            OptionalInt64(summary, "count")));
    }

    private static IReadOnlyList<ResearchEvidenceUncertaintyDto> DescriptiveUncertainty(
        JsonElement report,
        double? confidenceLevel)
    {
        var result = new List<ResearchEvidenceUncertaintyDto>();
        if (!report.TryGetProperty("eventTypes", out var eventTypes) || eventTypes.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var eventType in eventTypes.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            if (!EvidenceNamePattern().IsMatch(eventType.Name)
                || !eventType.Value.TryGetProperty("horizons", out var horizons)
                || horizons.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var horizon in horizons.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (!horizon.Value.TryGetProperty("metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var metric in metrics.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    foreach (var comparison in new[] { "event", "matchedControlDifference" })
                    {
                        if (!metric.Value.TryGetProperty(comparison, out var summary)
                            || OptionalObjectInterval(summary, "meanBlockBootstrapInterval") is not { } interval)
                            continue;
                        result.Add(new ResearchEvidenceUncertaintyDto(
                            $"{eventType.Name}.{horizon.Name}.{metric.Name}.{comparison}.mean",
                            interval.Lower,
                            interval.Upper,
                            confidenceLevel,
                            false));
                    }
                }
            }
        }
        return result;
    }

    private byte[] ReadBounded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > _maxArtifactBytes)
            throw new InvalidDataException("Evidence artifact is missing, empty, or exceeds the size limit.");
        return File.ReadAllBytes(path);
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string HashBytes(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static ResearchEvidenceDatasetDto DatasetFrom(JsonElement manifest)
    {
        var data = RequiredProperty(manifest, "dataProvenance");
        return new ResearchEvidenceDatasetDto(
            SafeDescriptor(OptionalString(data, "source")) ?? "versioned immutable research input",
            OptionalInt64(data, "rowCount"),
            OptionalInt64(data, "firstDecisionTimeMs") ?? OptionalInt64(data, "firstOpenTimeMs"),
            OptionalInt64(data, "lastDecisionTimeMs") ?? OptionalInt64(data, "lastCloseTimeMs"),
            OptionalString(data, "datasetSha256"));
    }

    private static ResearchEvidenceProtocolDto ProtocolFrom(JsonElement manifest, JsonElement evaluation)
    {
        string? multipleTesting = null;
        if (evaluation.TryGetProperty("multipleComparison", out var comparison))
            multipleTesting = OptionalString(comparison, "method");
        else if (evaluation.TryGetProperty("multipleTesting", out var testing))
            multipleTesting = OptionalString(testing, "method");
        else if (evaluation.TryGetProperty("familywiseControl", out var familywise))
            multipleTesting = OptionalString(familywise, "method");
        else if (evaluation.TryGetProperty("trials", out var trials) && trials.ValueKind == JsonValueKind.Array)
            multipleTesting = "Bonferroni over the declared candidate family";
        else if (evaluation.TryGetProperty("pairedBrierSensitivity", out _))
            multipleTesting = "Predeclared paired circular block-bootstrap sensitivity checks";

        return new ResearchEvidenceProtocolDto(
            OptionalString(evaluation, "evaluatorVersion")
                ?? (manifest.TryGetProperty("parameters", out var parameters) ? OptionalString(parameters, "evaluatorVersion") : null),
            OptionalString(manifest, "decisionTime"),
            OptionalString(manifest, "outcomePriceBasis"),
            OptionalBoolean(evaluation, "chronologicalOos") ?? true,
            multipleTesting);
    }

    private static IReadOnlyList<ResearchEvidenceBaselineDto> BaselinesFrom(string kind, JsonElement evaluation)
    {
        var result = new List<ResearchEvidenceBaselineDto>();
        if (evaluation.TryGetProperty("baselineDefinitions", out var definitions))
        {
            foreach (var property in definitions.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                if (property.Value.ValueKind == JsonValueKind.String)
                    result.Add(new ResearchEvidenceBaselineDto(property.Name, property.Value.GetString()!));
        }
        else if (kind == "feature-groups")
        {
            result.Add(new ResearchEvidenceBaselineDto("rolling_class_prior", "Laplace-smoothed expanding class prior available at each decision time."));
        }
        else if (kind == "technical-events")
        {
            result.Add(new ResearchEvidenceBaselineDto("causal_context_prior", "All prior BTCUSDT 4h bars in the same causal six-close trend context whose outcomes were available by the event decision."));
        }
        return result;
    }

    private static IReadOnlyList<ResearchEvidenceMetricDto> MetricsFrom(string kind, JsonElement evaluation) => kind switch
    {
        "ml-v3" => MlV3Metrics(evaluation),
        "ml-v2" => MlMetrics(evaluation),
        "feature-groups" => FeatureMetrics(evaluation),
        "technical-events" => TechnicalEventMetrics(evaluation),
        _ => []
    };

    private static IReadOnlyList<ResearchEvidenceFindingDto> FindingsFrom(string kind, JsonElement evaluation) => kind switch
    {
        "ml-v3" => V3Findings(evaluation),
        "ml-v2" => V2Findings(evaluation),
        "feature-groups" => FeatureFindings(evaluation),
        "technical-events" => TechnicalEventFindings(evaluation),
        _ => []
    };

    private static IReadOnlyList<ResearchEvidenceFindingDto> V3Findings(JsonElement evaluation)
    {
        var result = new List<ResearchEvidenceFindingDto>();
        var evaluationRows = OptionalInt64(evaluation, "evaluationRows");
        if (evaluation.TryGetProperty("baselineComparisons", out var comparisons)
            && comparisons.ValueKind == JsonValueKind.Array)
        {
            foreach (var comparison in comparisons.EnumerateArray())
            {
                var baseline = OptionalString(comparison, "baseline") ?? "unknown";
                if (!comparison.TryGetProperty("metrics", out var metricResults)
                    || metricResults.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var metric in metricResults.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    var meanLift = OptionalDouble(metric.Value, "meanLift");
                    if (!metric.Value.TryGetProperty("intervals", out var intervals)
                        || intervals.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var item in intervals.EnumerateArray())
                    {
                        var blockSize = OptionalInt64(item, "blockSizeRows");
                        var interval = OptionalInterval(item, "ci");
                        result.Add(new ResearchEvidenceFindingDto(
                            $"baseline:{baseline}:{metric.Name}:block:{blockSize}",
                            $"{baseline} {metric.Name} lift, block {blockSize}",
                            V3IntervalStatus(OptionalString(item, "status")),
                            $"{metric.Name}Lift",
                            meanLift,
                            interval?.Lower,
                            interval?.Upper,
                            evaluationRows));
                    }
                }
            }
        }

        if (evaluation.TryGetProperty("gateChecks", out var gateChecks)
            && gateChecks.ValueKind == JsonValueKind.Object)
        {
            foreach (var check in gateChecks.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                var passed = check.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => (bool?)null
                };
                if (passed is null)
                    continue;
                result.Add(new ResearchEvidenceFindingDto(
                    $"gate:{check.Name}", check.Name, passed.Value ? "passed" : "failed",
                    "gateCheck", passed.Value ? 1d : 0d, null, null, evaluationRows));
            }
        }

        if (evaluation.TryGetProperty("hgbFeatureGroupAblation", out var ablations)
            && ablations.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ablations.EnumerateArray())
            {
                var group = OptionalString(item, "group") ?? "unknown";
                var interval = OptionalInterval(item, "familywiseCi");
                result.Add(new ResearchEvidenceFindingDto(
                    $"hgb-ablation:{group}", group, V3AblationStatus(OptionalString(item, "interpretation")),
                    "meanBrierDamageWhenOmitted", OptionalDouble(item, "meanBrierDamageWhenOmitted"),
                    interval?.Lower, interval?.Upper, evaluationRows));
            }
        }
        return result;
    }

    private static string V3IntervalStatus(string? status) => status switch
    {
        "positive" => "positive",
        "negative" => "negative",
        _ => "inconclusive"
    };

    private static string V3AblationStatus(string? status) => status switch
    {
        "useful" => "useful",
        "harmful" => "harmful",
        _ => "inconclusive"
    };

    private static IReadOnlyList<ResearchEvidenceFindingDto> V2Findings(JsonElement evaluation)
    {
        var result = new List<ResearchEvidenceFindingDto>();
        if (!evaluation.TryGetProperty("trials", out var trials) || trials.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in trials.EnumerateArray())
        {
            var trial = OptionalString(item, "trial") ?? "unknown";
            var metrics = item.TryGetProperty("metrics", out var metricObject) ? metricObject : default;
            var interval = OptionalInterval(item, "pairedBrierLiftFamilywiseCi");
            result.Add(new ResearchEvidenceFindingDto(
                $"trial:{trial}", trial, OptionalString(item, "status") ?? "reference",
                "brier", metrics.ValueKind == JsonValueKind.Object ? OptionalDouble(metrics, "brier") : null,
                interval?.Lower, interval?.Upper,
                metrics.ValueKind == JsonValueKind.Object ? OptionalInt64(metrics, "samples") : null));
        }
        return result;
    }

    private static IReadOnlyList<ResearchEvidenceFindingDto> FeatureFindings(JsonElement evaluation)
    {
        var result = new List<ResearchEvidenceFindingDto>();
        if (!evaluation.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var group in groups.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            var interval = OptionalInterval(group.Value, "incrementalContributionFamilywiseCi");
            result.Add(new ResearchEvidenceFindingDto(
                $"feature-group:{group.Name}", group.Name,
                OptionalString(group.Value, "incrementalStatus") ?? "inconclusive",
                "incrementalBrierContribution", OptionalDouble(group.Value, "incrementalBrierContribution"),
                interval?.Lower, interval?.Upper, OptionalInt64(evaluation, "evaluationRows")));
        }
        return result;
    }

    private static IReadOnlyList<ResearchEvidenceFindingDto> TechnicalEventFindings(JsonElement evaluation)
    {
        var result = new List<ResearchEvidenceFindingDto>();
        if (!evaluation.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var module in modules.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            if (!module.Value.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var item in events.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                JsonElement comparison = default;
                var hasComparison = item.Value.TryGetProperty("causalExpandingPriorComparison", out comparison);
                var interval = hasComparison ? OptionalInterval(comparison, "pairedBrierLiftBlockBootstrapCi") : null;
                result.Add(new ResearchEvidenceFindingDto(
                    $"{module.Name}:{item.Name}", item.Name,
                    OptionalString(item.Value, "status") ?? "inconclusive",
                    "pairedBrierLift", hasComparison ? OptionalDouble(comparison, "pairedBrierLift") : null,
                    interval?.Lower, interval?.Upper, OptionalInt64(item.Value, "evaluatedOosOccurrences")));
            }
        }
        return result;
    }

    private static IReadOnlyList<ResearchEvidenceMetricDto> MlV3Metrics(JsonElement evaluation)
    {
        var result = new List<ResearchEvidenceMetricDto>();
        if (!evaluation.TryGetProperty("metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Object)
            return result;
        const string candidate = "hist_gradient_boosting";
        var fallbackBaseline = OptionalString(evaluation, "strongestBaseline");
        string? brierBaseline = fallbackBaseline;
        string? logLossBaseline = fallbackBaseline;
        if (evaluation.TryGetProperty("strongestBaselineByMetric", out var strongest)
            && strongest.ValueKind == JsonValueKind.Object)
        {
            brierBaseline = OptionalString(strongest, "brier") ?? brierBaseline;
            logLossBaseline = OptionalString(strongest, "logLoss") ?? logLossBaseline;
        }
        if (metrics.TryGetProperty(candidate, out var candidateMetrics))
        {
            AddMetric(result, $"{candidate}.brier", BaselineAwareLabel("HGB Brier", brierBaseline), OptionalDouble(candidateMetrics, "brier"), "score", brierBaseline);
            AddMetric(result, $"{candidate}.logLoss", BaselineAwareLabel("HGB log loss", logLossBaseline), OptionalDouble(candidateMetrics, "logLoss"), "score", logLossBaseline);
            AddMetric(result, $"{candidate}.balancedAccuracy", "HGB balanced accuracy", OptionalDouble(candidateMetrics, "balancedAccuracy"), "ratio", null);
        }
        if (brierBaseline is not null && metrics.TryGetProperty(brierBaseline, out var brierBaselineMetrics))
        {
            AddMetric(result, $"{brierBaseline}.brier", $"{brierBaseline} Brier", OptionalDouble(brierBaselineMetrics, "brier"), "score", null);
        }
        if (logLossBaseline is not null && metrics.TryGetProperty(logLossBaseline, out var logLossBaselineMetrics))
            AddMetric(result, $"{logLossBaseline}.logLoss", $"{logLossBaseline} log loss", OptionalDouble(logLossBaselineMetrics, "logLoss"), "score", null);
        if (evaluation.TryGetProperty("pairedBrierSensitivity", out var sensitivity)
            && sensitivity.ValueKind == JsonValueKind.Array && sensitivity.GetArrayLength() > 0)
            AddMetric(result, "pairedBrierLift", BaselineAwareLabel("Paired Brier lift", brierBaseline), OptionalDouble(sensitivity[0], "meanPairedBrierLift"), "score", brierBaseline);
        if (evaluation.TryGetProperty("hgbFeatureGroupAblation", out var ablations)
            && ablations.ValueKind == JsonValueKind.Array)
        {
            foreach (var ablation in ablations.EnumerateArray())
            {
                var group = OptionalString(ablation, "group");
                if (group is not null)
                    AddMetric(result, $"group.{group}.brierDamageWhenOmitted", $"{group} Brier damage when omitted",
                        OptionalDouble(ablation, "meanBrierDamageWhenOmitted"), "score", "full HGB");
            }
        }
        return result;

        static string BaselineAwareLabel(string label, string? baseline) =>
            baseline is null ? label : $"{label} vs {baseline}";
    }

    private static IReadOnlyList<ResearchEvidenceMetricDto> MlMetrics(JsonElement evaluation)
    {
        var selected = OptionalString(evaluation, "selectedCandidate");
        var baseline = OptionalString(evaluation, "strongestBaseline");
        var selectedTrial = FindTrial(evaluation, selected);
        var baselineTrial = FindTrial(evaluation, baseline);
        var result = new List<ResearchEvidenceMetricDto>();
        AddTrialMetrics(result, selectedTrial, selected ?? "candidate", baseline);
        AddTrialMetrics(result, baselineTrial, baseline ?? "baseline", null);
        if (selectedTrial is { } trial)
            AddMetric(result, "pairedBrierLift", "Paired Brier lift", OptionalDouble(trial, "pairedBrierLift"), "score", baseline);
        return result;
    }

    private static void AddTrialMetrics(List<ResearchEvidenceMetricDto> result, JsonElement? trial, string prefix, string? baseline)
    {
        if (trial is not { } value || !value.TryGetProperty("metrics", out var metrics))
            return;
        AddMetric(result, $"{prefix}.brier", $"{prefix} Brier", OptionalDouble(metrics, "brier"), "score", baseline);
        AddMetric(result, $"{prefix}.logLoss", $"{prefix} log loss", OptionalDouble(metrics, "logLoss"), "score", baseline);
        AddMetric(result, $"{prefix}.balancedAccuracy", $"{prefix} balanced accuracy", OptionalDouble(metrics, "balancedAccuracy"), "ratio", baseline);
    }

    private static IReadOnlyList<ResearchEvidenceMetricDto> FeatureMetrics(JsonElement evaluation)
    {
        var result = new List<ResearchEvidenceMetricDto>();
        if (evaluation.TryGetProperty("baseline", out var baseline))
            AddMetric(result, "baseline.brier", "Rolling-prior Brier", OptionalDouble(baseline, "brier"), "score", null);
        if (evaluation.TryGetProperty("fullModel", out var full))
        {
            AddMetric(result, "full.brier", "Full-model Brier", OptionalDouble(full, "brier"), "score", "rolling_class_prior");
            AddMetric(result, "full.brierLift", "Full-model Brier lift", OptionalDouble(full, "brierLiftVsRollingPrior"), "score", "rolling_class_prior");
            AddMetric(result, "full.balancedAccuracy", "Full-model balanced accuracy", OptionalDouble(full, "balancedAccuracy"), "ratio", null);
        }
        if (evaluation.TryGetProperty("groups", out var groups))
        {
            foreach (var group in groups.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                AddMetric(result, $"group.{group.Name}.incrementalBrier", $"{group.Name} incremental Brier contribution",
                    OptionalDouble(group.Value, "incrementalBrierContribution"), "score", "full model without group");
        }
        return result;
    }

    private static IReadOnlyList<ResearchEvidenceMetricDto> TechnicalEventMetrics(JsonElement evaluation)
    {
        var result = new List<ResearchEvidenceMetricDto>();
        AddMetric(result, "closedBars", "Closed bars", OptionalDouble(evaluation, "totalClosedBars"), "count", null);
        AddMetric(result, "eligibleOutcomeBars", "Eligible outcome bars", OptionalDouble(evaluation, "eligibleOutcomeBars"), "count", null);
        if (evaluation.TryGetProperty("multipleTesting", out var testing))
            AddMetric(result, "declaredTrials", "Declared event trials", OptionalDouble(testing, "declaredTrialCount"), "count", null);
        if (evaluation.TryGetProperty("modules", out var modules))
        {
            foreach (var module in modules.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                AddMetric(result, $"module.{module.Name}.eventTypes", $"{module.Name} event types",
                    OptionalDouble(module.Value, "eventTypeCount"), "count", null);
        }
        return result;
    }

    private static void AddMetric(List<ResearchEvidenceMetricDto> result, string name, string label, double? value, string unit, string? baseline)
    {
        if (value is not null && double.IsFinite(value.Value))
            result.Add(new ResearchEvidenceMetricDto(name, label, value, unit, baseline));
    }

    private static IReadOnlyList<ResearchEvidenceUncertaintyDto> UncertaintyFrom(string kind, JsonElement evaluation)
    {
        var result = new List<ResearchEvidenceUncertaintyDto>();
        if (kind == "ml-v2")
        {
            var trial = FindTrial(evaluation, OptionalString(evaluation, "selectedCandidate"));
            AddInterval(result, "selectedCandidate.pairedBrierLift", trial, "pairedBrierLiftFamilywiseCi",
                trial is { } value && value.TryGetProperty("familywiseControl", out var control)
                    ? OptionalDouble(control, "confidenceLevel") : null, true);
        }
        else if (kind == "ml-v3")
        {
            if (evaluation.TryGetProperty("pairedBrierSensitivity", out var sensitivity)
                && sensitivity.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in sensitivity.EnumerateArray())
                {
                    var blockSize = OptionalInt64(item, "blockSizeRows");
                    AddInterval(result, $"pairedBrierLift.block{blockSize}", item, "ci",
                        OptionalDouble(item, "confidenceLevel"), false);
                }
            }
            if (evaluation.TryGetProperty("hgbFeatureGroupAblation", out var ablations)
                && ablations.ValueKind == JsonValueKind.Array)
                foreach (var item in ablations.EnumerateArray())
                    AddInterval(result, $"group.{OptionalString(item, "group")}.brierDamageWhenOmitted", item,
                        "familywiseCi", null, true);
        }
        else if (kind == "feature-groups")
        {
            AddInterval(result, "fullModel.brierLift", evaluation.TryGetProperty("fullModel", out var full) ? full : null,
                "brierLiftFamilywiseCi", null, true);
            if (evaluation.TryGetProperty("groups", out var groups))
                foreach (var group in groups.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                    AddInterval(result, $"group.{group.Name}.incrementalBrier", group.Value,
                        "incrementalContributionFamilywiseCi", null, true);
        }
        return result;
    }

    private static void AddInterval(
        List<ResearchEvidenceUncertaintyDto> result,
        string name,
        JsonElement? container,
        string property,
        double? confidence,
        bool familywise)
    {
        if (container is not { } value || OptionalInterval(value, property) is not { } interval)
            return;
        result.Add(new ResearchEvidenceUncertaintyDto(
            name, interval.Lower, interval.Upper, confidence, familywise));
    }

    private static ResearchEvidenceCoverageDto CoverageFrom(string kind, JsonElement evaluation)
    {
        var evaluated = OptionalInt64(evaluation, "evaluationRows") ?? OptionalInt64(evaluation, "eligibleOutcomeBars");
        var eligible = OptionalInt64(evaluation, "eligibleRowsAfterWarmup") ?? evaluated;
        var ratio = OptionalDouble(evaluation, "coverage") ?? OptionalDouble(evaluation, "protocolCoverage");
        int? folds = evaluation.TryGetProperty("folds", out var foldArray) && foldArray.ValueKind == JsonValueKind.Array
            ? foldArray.GetArrayLength() : null;
        return new ResearchEvidenceCoverageDto(evaluated, eligible, ratio, folds);
    }

    private static ResearchEvidenceProvenanceDto ProvenanceFrom(JsonElement manifest)
    {
        JsonElement code = default;
        var hasCode = manifest.TryGetProperty("codeProvenance", out code);
        JsonElement git = default;
        var hasGit = hasCode && code.TryGetProperty("git", out git);
        return new ResearchEvidenceProvenanceDto(
            OptionalString(manifest, "contractVersion") ?? OptionalString(manifest, "schemaVersion"),
            SafeDescriptor(OptionalString(manifest, "experiment")),
            hasCode ? OptionalString(code, "v3EvaluatorSha256") ?? OptionalString(code, "evaluatorSha256") : null,
            hasCode ? OptionalString(code, "researchContractSha256") : null,
            hasGit ? SafeDescriptor(OptionalString(git, "commit")) : null,
            hasGit ? OptionalBoolean(git, "dirty") : null);
    }

    private static string StatusFor(string kind, JsonElement evaluation, EvidenceIntegrityDto integrity)
    {
        if (!integrity.ReportHashEmbedded)
            return ResearchEvidenceStatuses.IntegrityLimited;
        if (kind == "ml-v2" && evaluation.TryGetProperty("promotionGate", out var gate)
            && OptionalBoolean(gate, "passed") == true)
            return ResearchEvidenceStatuses.Supported;
        return ResearchEvidenceStatuses.Inconclusive;
    }

    private static string SummaryFor(string kind, JsonElement evaluation) => kind switch
    {
        "ml-v3" => $"Selection-aware retrospective HGB screen with a frozen dataset and row predictions for {OptionalInt64(evaluation, "evaluationRows") ?? 0:N0} BTCUSDT 4h decisions.",
        "ml-v2" => $"Walk-forward model comparison on {OptionalInt64(evaluation, "evaluationRows") ?? 0:N0} out-of-sample BTCUSDT 4h decisions; the selected candidate is {OptionalString(evaluation, "selectedCandidate") ?? "unavailable"}.",
        "feature-groups" => "Ablation measures feature-group contribution within a fixed logistic model; the full model did not beat the rolling prior.",
        "technical-events" => "Candle, volume and regime events were tested against causal context priors with familywise control; no event family established approved predictive evidence.",
        _ => "Research evidence artifact."
    };

    private static string HypothesisFor(string kind) => kind switch
    {
        "ml-v3" => "A causally computed HGB feature model improves proper probabilistic scores over predeclared historical, adaptive, momentum and reversion baselines on future walk-forward blocks.",
        "ml-v2" => "Causal features available after a finalized BTCUSDT 4h bar improve next-bar three-class probability forecasts over predeclared baselines.",
        "feature-groups" => "A declared feature group adds incremental probabilistic information within the fixed full logistic model.",
        "technical-events" => "A predeclared technical event improves next-bar conditional forecasts over the causal expanding context prior.",
        _ => "No hypothesis declared."
    };

    private static IReadOnlyList<string> LimitationsFor(string kind) => kind switch
    {
        "ml-v3" =>
        [
            "This is historical predictive evidence for one BTCUSDT 4h target; it does not establish trading profitability.",
            "Brier score combines calibration and discrimination and must be interpreted with reliability and log loss.",
            "Feature-group ablation is conditional on the fixed HGB protocol and is not a causal market-effect estimate.",
            "HGB was selected using earlier overlapping v2 out-of-sample history, so this bundle is not an independent confirmatory test and is not promotion evidence.",
            "Fees, slippage, latency, fills, capacity, paper outcomes and live outcomes are outside this evidence tier."
        ],
        "ml-v2" =>
        [
            "This is historical predictive evidence, not an execution, PnL, paper-trading or live-trading claim.",
            "Overlapping windows and market dependence mean the evaluated rows are not independent observations.",
            "The rolling-class baseline is an expanding available-label prior; adaptive-regime baselines remain a separate research question."
        ],
        "feature-groups" =>
        [
            "Ablation is specific to the fixed logistic model and cannot establish that a group is useful or harmful for HGB or other model families.",
            "The full logistic model did not beat the rolling-prior baseline, so group results are internal diagnostics rather than promotion evidence.",
            "No execution or PnL claim is made."
        ],
        "technical-events" =>
        [
            "Conditional next-bar evidence is not a tradable strategy or PnL estimate.",
            "Causal SMC was unavailable because legacy rows lacked valid point-in-time availability lineage.",
            "Event occurrences and overlapping market periods are dependent observations."
        ],
        _ => []
    };

    private static string ConclusionFor(string kind, JsonElement evaluation, EvidenceIntegrityDto integrity)
    {
        if (!integrity.ReportHashEmbedded)
            return "The result is available for audit with limited report integrity; it is not eligible for automated promotion.";
        return kind switch
        {
            "ml-v2" when evaluation.TryGetProperty("promotionGate", out var gate) && OptionalBoolean(gate, "passed") == true =>
                "The selected candidate passed this artifact's historical predictive gate. Economic simulation and prospective evidence are still required before any trading claim.",
            "ml-v3" when evaluation.TryGetProperty("promotionGate", out var v3Gate) && OptionalBoolean(v3Gate, "passed") == true =>
                "The HGB candidate passed the declared retrospective selection-aware screen, but this is not an independent confirmatory result and cannot authorize promotion or a trading claim.",
            "feature-groups" => "The full logistic model did not beat the rolling prior; group effects are diagnostics only.",
            "technical-events" => "No tested event established familywise predictive evidence; unavailable modules remain unassessed.",
            _ => "The evidence is inconclusive."
        };
    }

    private static JsonElement? FindTrial(JsonElement evaluation, string? trialName)
    {
        if (trialName is null || !evaluation.TryGetProperty("trials", out var trials) || trials.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var trial in trials.EnumerateArray())
            if (string.Equals(OptionalString(trial, "trial"), trialName, StringComparison.Ordinal))
                return trial;
        return null;
    }

    internal static string ComputeCanonicalHash(JsonElement element, string excludedRootProperty)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            // Python's json.dumps canonical form does not HTML-escape '+', '<',
            // '>' or '&'. Evaluator manifests are ASCII, so the relaxed encoder
            // matches its sort_keys/separators representation byte-for-byte.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false
        }))
        {
            WriteCanonical(writer, element, excludedRootProperty, isRoot: true);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, string excludedRootProperty, bool isRoot)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (isRoot && string.Equals(property.Name, excludedRootProperty, StringComparison.Ordinal))
                        continue;
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, excludedRootProperty, false);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item, excludedRootProperty, false);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException("Unsupported JSON value in evidence artifact.");
        }
    }

    private static bool FixedTimeEquals(string actual, string expected) =>
        actual.Length == expected.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected));

    private static JsonElement RequiredProperty(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value
            : throw new InvalidDataException($"Evidence property '{property}' is missing.");

    private static string RequiredString(JsonElement element, string property)
    {
        var value = RequiredProperty(element, property);
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Evidence property '{property}' is invalid.");
    }

    private static void RequireObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Evidence '{name}' must be an object.");
    }

    private static string? OptionalString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? OptionalInt64(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed) ? parsed : null;

    private static double? OptionalDouble(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed) && double.IsFinite(parsed) ? parsed : null;

    private static EvidenceInterval? OptionalInterval(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() != 2)
            return null;

        var lower = value[0].ValueKind == JsonValueKind.Number && value[0].TryGetDouble(out var parsedLower)
            && double.IsFinite(parsedLower) ? parsedLower : (double?)null;
        var upper = value[1].ValueKind == JsonValueKind.Number && value[1].TryGetDouble(out var parsedUpper)
            && double.IsFinite(parsedUpper) ? parsedUpper : (double?)null;
        return lower is not null && upper is not null ? new EvidenceInterval(lower.Value, upper.Value) : null;
    }

    private static EvidenceInterval? OptionalObjectInterval(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Object)
            return null;
        var lower = OptionalDouble(value, "lower");
        var upper = OptionalDouble(value, "upper");
        return lower is not null && upper is not null ? new EvidenceInterval(lower.Value, upper.Value) : null;
    }

    private static bool? OptionalBoolean(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    private static DateTime? OptionalUtcDateTime(JsonElement element, string property)
    {
        var value = OptionalString(element, property);
        if (value is null)
            return null;
        if (!DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
            throw new InvalidDataException($"Evidence timestamp '{property}' is invalid.");
        return parsed.UtcDateTime;
    }

    private static string? SafeDescriptor(string? value) =>
        value is not null && SafeDescriptorPattern().IsMatch(value) ? value : null;

    private static readonly JsonDocumentOptions StrictJsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64
    };

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^([a-f0-9]{64})\\.report\\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex ReportNamePattern();

    [GeneratedRegex("^([a-f0-9]{64})\\.manifest\\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex BundleManifestNamePattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:+-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeDescriptorPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_:-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex EvidenceNamePattern();

    private sealed record EvidenceKindDefinition(string Kind, string Title);
    private sealed record EvidenceInterval(double Lower, double Upper);
    private sealed record LoadedArtifact(
        string Id,
        string Kind,
        DateTime? CreatedAtUtc,
        ResearchEvidenceCatalogItemDto Item,
        ResearchEvidenceDetailDto Detail);
    private sealed record AtomicBundleReference(
        string Timeframe,
        long CutoffMs,
        string ManifestFileName,
        string ManifestSha256,
        long Stored,
        long Eligible,
        long Excluded,
        long RealizedAtMaxHorizon);
    private sealed record AtomicTechnicalRun(
        string RunIndexFileName,
        string RunIndexSha256,
        string ContractDefinitionsSha256,
        IReadOnlyList<AtomicBundleReference> Bundles,
        IReadOnlyList<LoadedArtifact> Artifacts);
    private sealed record LoadResult(
        IReadOnlyList<LoadedArtifact> Artifacts,
        int Scanned,
        int Rejected,
        AtomicTechnicalRun? AtomicRun);
}
