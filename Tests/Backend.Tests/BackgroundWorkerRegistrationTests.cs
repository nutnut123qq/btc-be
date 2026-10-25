using Backend.Controllers;
using Backend.Data;
using Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

// Inspects the real IHostedService registrations produced by AddBackgroundWorkers; nothing is started.
public class BackgroundWorkerRegistrationTests
{
    private static readonly Type[] SwitchableWorkers =
    [
        typeof(KlinesIngestionWorker), typeof(IndexingBackgroundWorker), typeof(CausalSmartMoneyRebuildWorker),
        typeof(RssIngestionService), typeof(EmbeddingBackfillWorker), typeof(MlDatasetBuilder), typeof(WindowDatasetBuilder)
    ];

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static List<Type?> HostedTypes(IConfiguration configuration)
    {
        var services = new ServiceCollection().AddBackgroundWorkers(configuration);
        return services.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType).ToList();
    }

    [Fact]
    public void GateOff_RegistersNoWorkers_EvenWhenChildFlagsAreOn()
    {
        var hosted = HostedTypes(Config(("BackgroundWorkers:Enabled", "false"),
            ("BackgroundWorkers:KlinesIngestionWorker:Enabled", "true"), ("BackgroundWorkers:RssIngestionService:Enabled", "true")));

        Assert.Empty(hosted);
    }

    [Fact]
    public void GateOnWithoutChildFlags_KeepsEveryDefaultWorker()
    {
        var hosted = HostedTypes(Config());

        foreach (var worker in SwitchableWorkers.Append(typeof(PriceAlertWorker)).Append(typeof(EquityIngestionWorker)).Append(typeof(EquityDocumentPipelineWorker)))
            Assert.Single(hosted, t => t == worker);
        // The Binance user-data stream is registered through a factory (no ImplementationType).
        Assert.Single(hosted, t => t is null);
        Assert.Equal(11, hosted.Count);
    }

    [Fact]
    public void GateOnChildFalse_DropsOnlyThatWorker()
    {
        var hosted = HostedTypes(Config(("BackgroundWorkers:Enabled", "true"),
            ("BackgroundWorkers:RssIngestionService:Enabled", "false"), ("BackgroundWorkers:EmbeddingBackfillWorker:Enabled", "false")));

        Assert.DoesNotContain(typeof(RssIngestionService), hosted);
        Assert.DoesNotContain(typeof(EmbeddingBackfillWorker), hosted);
        foreach (var worker in SwitchableWorkers.Except([typeof(RssIngestionService), typeof(EmbeddingBackfillWorker)]))
            Assert.Single(hosted, t => t == worker);
        Assert.Equal(9, hosted.Count);
    }

    [Fact]
    public void GateOffWithDocumentWorkerStandalone_RegistersOnlyTheDocumentWorker()
    {
        var hosted = HostedTypes(Config(("BackgroundWorkers:Enabled", "false"), ("EquityDocuments:WorkerStandalone", "true")));

        Assert.Equal([typeof(EquityDocumentPipelineWorker)], hosted);
    }

    [Fact]
    public async Task Health_ReportsEveryWorkerDisabledWhenGateIsOff()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options);
        var controller = new HealthController(db, NullLogger<HealthController>.Instance,
            configuration: Config(("BackgroundWorkers:Enabled", "false"), ("BackgroundWorkers:KlinesIngestionWorker:Enabled", "true")));

        var ok = Assert.IsType<OkObjectResult>((await controller.Workers(default)).Result);
        var body = Assert.IsType<WorkerHealthResponse>(ok.Value);

        Assert.All(body.Workers, w => Assert.Equal("disabled", w.Status));
        Assert.Contains("BackgroundWorkers:Enabled=false", body.Workers.Single(w => w.Name == "KlinesIngestionWorker").Message);
    }
}
