namespace Backend.Services;

/// <summary>
/// Where issuer source documents are stored (EQX v2 §C1; originals are never committed) and how the
/// v3 document pipeline subprocess is launched/scheduled (spec-v3-document-pipeline.md §1/§4).
/// </summary>
public sealed class EquityDocumentsOptions
{
    public const string SectionName = "EquityDocuments";

    /// <summary>Absolute, or relative to the backend content root (default: workspace data/equity-documents).</summary>
    public string RootPath { get; set; } = Path.Combine("..", "data", "equity-documents");

    /// <summary>Python interpreter (absolute or relative to the content root). Run with <c>-I</c>.</summary>
    public string PythonPath { get; set; } = Path.Combine("..", "ai", "venv", "Scripts", "python.exe");

    /// <summary>Pipeline entry point (absolute or relative to the content root).</summary>
    public string PipelineScript { get; set; } = Path.Combine("..", "ai", "equity_pipeline.py");

    /// <summary>Daily incremental worker switch. Default false — not enabled in any appsettings.</summary>
    public bool PipelineEnabled { get; set; }

    /// <summary>Daily worker poll time, HH:mm Asia/Ho_Chi_Minh.</summary>
    public string PipelineRunAtLocal { get; set; } = "18:30";

    /// <summary>Symbols the worker polls (sequentially, failure isolated per symbol).</summary>
    public string[] PipelineSymbols { get; set; } = ["BID", "MBB"];

    public double BackfillTimeoutMinutes { get; set; } = 360;
    public double IncrementalTimeoutMinutes { get; set; } = 45;

    /// <summary>A running row whose heartbeat is older than this is marked failed (stale-heartbeat) before a new run.</summary>
    public double StaleHeartbeatMinutes { get; set; } = 15;

    /// <summary>Supervisor heartbeat while the subprocess is alive (process liveness, not data progress).</summary>
    public double SupervisorHeartbeatSeconds { get; set; } = 60;

    /// <summary>Optional default request budget / document cap passed as --budget / --max-docs to worker runs.</summary>
    public int? WorkerBudget { get; set; }
    public int? WorkerMaxDocs { get; set; }

    /// <summary>--extract-scope for worker runs: consolidated (default) | all.</summary>
    public string WorkerExtractScope { get; set; } = EquityPipelineExtractScopes.Consolidated;

    /// <summary>Extra environment for the subprocess (e.g. OCR tool paths). DB_* keys are always set by the backend.</summary>
    public Dictionary<string, string> ExtraEnv { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Financial-fact settings (spec v3 §3): which metrics coverage expects per group.</summary>
public sealed class EquityFactsOptions
{
    public const string SectionName = "EquityFacts";

    /// <summary>Metric catalog written by the Python normalizer; absolute or relative to the content root.</summary>
    public string MetricCatalogPath { get; set; } = Path.Combine("..", "ai", "equity_facts", "metric_catalog.json");
}

public static class EquityResearchServiceCollectionExtensions
{
    /// <summary>
    /// Research snapshot/readiness, session research notes, source documents (EQX v2 §B/§C) and the v3
    /// document pipeline orchestration + financial facts/coverage (spec v3 §4).
    /// Relies on the equity-lane registrations already in Program.cs (AppDbContext, VietnamMarketCalendar,
    /// TimeProvider, IResearchEvidenceCatalog, EvidenceCatalogOptions, "AIService" HttpClient, IMemoryCache).
    /// The daily <see cref="EquityDocumentPipelineWorker"/> is registered separately (BackgroundWorkers-gated).
    /// </summary>
    public static IServiceCollection AddEquityResearch(this IServiceCollection services)
    {
        services.AddOptions<EquityDocumentsOptions>().BindConfiguration(EquityDocumentsOptions.SectionName);
        services.AddOptions<EquityFactsOptions>().BindConfiguration(EquityFactsOptions.SectionName);
        services.AddSingleton<EquityMetricCatalogProvider>();
        services.AddScoped<EquityResearchSnapshotService>();
        services.AddScoped<EquityResearchNoteService>();
        services.AddScoped<EquitySourceDocumentService>();
        services.AddScoped<EquityFinancialFactsService>();
        services.AddSingleton<EquityPipelineProcessRunner>();
        services.AddSingleton<IEquityPipelineRunner>(sp => sp.GetRequiredService<EquityPipelineProcessRunner>());
        services.AddScoped<EquityDocumentPipelineService>();
        return services;
    }
}
