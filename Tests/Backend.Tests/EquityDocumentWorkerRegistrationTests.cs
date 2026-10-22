using Backend.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Backend.Tests;

// EquityDocuments:WorkerStandalone registers ONLY the document worker when BackgroundWorkers:Enabled=false.
public class EquityDocumentWorkerRegistrationTests
{
    private sealed class Factory(bool backgroundWorkers, bool standalone) : TestWebApplicationFactory
    {
        public List<Type> Hosted { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("BackgroundWorkers:Enabled", backgroundWorkers.ToString());
            builder.UseSetting("EquityDocuments:WorkerStandalone", standalone.ToString());
            builder.ConfigureServices(services =>
            {
                // runs before the base callback, which removes every hosted service (InMemory DB, nothing runs)
                Hosted.AddRange(services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType is not null)
                    .Select(d => d.ImplementationType!));
            });
            base.ConfigureWebHost(builder);
        }
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, true)]
    public void Registration_FollowsFlags(bool backgroundWorkers, bool standalone, bool expectDocWorker, bool expectOthers)
    {
        using var factory = new Factory(backgroundWorkers, standalone);
        _ = factory.Services; // builds the host
        Assert.Equal(expectDocWorker, factory.Hosted.Contains(typeof(EquityDocumentPipelineWorker)));
        Assert.Equal(expectOthers, factory.Hosted.Contains(typeof(KlinesIngestionWorker)));
    }
}
