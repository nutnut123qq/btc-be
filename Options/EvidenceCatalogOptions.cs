namespace Backend.Options;

public sealed class EvidenceCatalogOptions
{
    public const string SectionName = "EvidenceCatalog";

    public string RootPath { get; set; } = "../ai/docs/research/evidence";
    public long MaxArtifactBytes { get; set; } = 4 * 1024 * 1024;
    public long MaxBundleArtifactBytes { get; set; } = 2L * 1024 * 1024 * 1024;
}
