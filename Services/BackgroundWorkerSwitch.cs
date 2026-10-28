namespace Backend.Services;

/// <summary>
/// Background worker registration. BackgroundWorkers:Enabled is the master gate; under it,
/// BackgroundWorkers:{WorkerName}:Enabled=false keeps that one hosted worker unregistered (default true = previous
/// behavior). Health reports a worker that is off by configuration as "disabled".
/// </summary>
public static class BackgroundWorkerSwitch
{
    public static bool GateEnabled(IConfiguration configuration) =>
        configuration.GetValue("BackgroundWorkers:Enabled", true);

    public static bool IsEnabled(IConfiguration configuration, string workerName) =>
        GateEnabled(configuration) && configuration.GetValue($"BackgroundWorkers:{workerName}:Enabled", true);

    public static IServiceCollection AddBackgroundWorkers(this IServiceCollection services, IConfiguration configuration)
    {
        // Maintenance/smoke-test mode can start the API without mutating background jobs.
        if (GateEnabled(configuration))
        {
            services.AddHostedService(sp => sp.GetRequiredService<BinanceUserDataStreamService>());
            AddIfEnabled<KlinesIngestionWorker>(services, configuration);
            AddIfEnabled<IndexingBackgroundWorker>(services, configuration);
            AddIfEnabled<CausalSmartMoneyRebuildWorker>(services, configuration);
            AddIfEnabled<RssIngestionService>(services, configuration);
            services.AddHostedService<PriceAlertWorker>();
            AddIfEnabled<EmbeddingBackfillWorker>(services, configuration);
            AddIfEnabled<MlDatasetBuilder>(services, configuration);
            AddIfEnabled<WindowDatasetBuilder>(services, configuration);
            // Read-only: keeps the evidence catalog verified from start on (maintenance/contract modes skip it via the gate).
            AddIfEnabled<ResearchEvidenceCatalogWarmup>(services, configuration);
            // Exits immediately unless EquityIngestion:Enabled=true (not enabled in any appsettings).
            services.AddHostedService<EquityIngestionWorker>();
            // Exits immediately unless EquityDocuments:PipelineEnabled=true (not enabled in any appsettings).
            services.AddHostedService<EquityDocumentPipelineWorker>();
        }
        else if (configuration.GetValue("EquityDocuments:WorkerStandalone", false))
        {
            // Explicit opt-in to run ONLY the document worker (local/ops verification) without starting every other
            // background job above; it still exits immediately unless EquityDocuments:PipelineEnabled=true.
            services.AddHostedService<EquityDocumentPipelineWorker>();
        }
        return services;
    }

    private static void AddIfEnabled<TWorker>(IServiceCollection services, IConfiguration configuration)
        where TWorker : class, IHostedService
    {
        if (IsEnabled(configuration, typeof(TWorker).Name)) services.AddHostedService<TWorker>();
    }
}
