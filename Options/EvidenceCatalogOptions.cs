namespace Backend.Options;

public sealed class EvidenceCatalogOptions
{
    public const string SectionName = "EvidenceCatalog";

    public string RootPath { get; set; } = "../ai/docs/research/evidence";
    public long MaxArtifactBytes { get; set; } = 4 * 1024 * 1024;
    public long MaxBundleArtifactBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    // Full artifact re-verification is expensive (hundreds of MB). The verified
    // catalog is served until the on-disk fingerprint changes or this interval
    // elapses; either re-verifies synchronously, so a served snapshot is always
    // younger than this interval and mtime-preserving tamper is detected no later
    // than it. A request in the second half of the interval re-verifies once in
    // the background (refresh ahead). 0 = verify on every request.
    public int CacheVerificationSeconds { get; set; } = 300;
}
