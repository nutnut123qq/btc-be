namespace Backend.Options;

public sealed class EvidenceCatalogOptions
{
    public const string SectionName = "EvidenceCatalog";

    public string RootPath { get; set; } = "../ai/docs/research/evidence";
    public long MaxArtifactBytes { get; set; } = 4 * 1024 * 1024;
    public long MaxBundleArtifactBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    // Full artifact re-verification is expensive (hundreds of MB). The verified
    // catalog is cached until the on-disk fingerprint changes or this interval
    // elapses, whichever comes first. mtime-preserving tamper is detected no
    // later than this interval.
    public int CacheVerificationSeconds { get; set; } = 300;
}
