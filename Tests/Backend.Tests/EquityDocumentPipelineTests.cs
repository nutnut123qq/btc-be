using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Backend.Tests;

/// <summary>
/// EQX v3 worker C: pipeline orchestration against a FAKE equity_pipeline.py (exit codes, timeout kill of the
/// process tree, cancel, busy 409, stale-heartbeat recovery, secrets/paths kept out of args and LogTail),
/// fundamentals coverage grid, financial-facts as-of filtering and HTTP wiring. EF InMemory + temp dirs; the
/// process tests need a Python interpreter (EQX_TEST_PYTHON or python on PATH) and return early without one.
/// </summary>
public class EquityDocumentPipelineTests : IDisposable
{
    private const string Password = "S3cret-Passw0rd!xyz";
    private const string ConnectionString = "Host=dbhost.test;Port=5544;Database=eqx_fake;Username=eqx_user;Password=" + Password;

    private const string FakeScript = """
        import os, sys, time, subprocess
        args = sys.argv[1:]
        mode = os.environ.get("FAKE_MODE", "exit0")
        symbol = args[args.index("--symbol") + 1] if "--symbol" in args else "?"
        print("argv=" + " ".join(args), flush=True)
        if os.environ.get("FAKE_FAIL_SYMBOL") == symbol:
            sys.stderr.write("forced failure for " + symbol + "\n")
            sys.exit(3)
        if mode == "env":
            for k in ["DB_HOST", "DB_PORT", "DB_USER", "DB_NAME", "OCR_TOOL"]:
                print(k + "=" + os.environ.get(k, "<unset>"), flush=True)
            print("PGPW_SET=" + str("PGPASSWORD" in os.environ), flush=True)
            print("HAS_PASS=" + str(os.environ.get("DB_PASS") == os.environ.get("EXPECT_PASS_CHECK", "")), flush=True)
            print("DOCS_ROOT=" + os.environ.get("EQUITY_DOCUMENTS_ROOT", "<unset>"), flush=True)
            sys.exit(0)
        if mode == "leak":
            print("DB_PASS=" + os.environ.get("DB_PASS", ""), flush=True)
            print("connecting with secret " + os.environ.get("DB_PASS", "") + " now", flush=True)
            print('Traceback: File "' + os.path.abspath(__file__) + '", line 1', flush=True)
            sys.exit(1)
        if mode == "exit2":
            sys.exit(2)
        if mode == "exit75":
            sys.exit(75)
        if mode == "exit3":
            sys.stderr.write("boom\n")
            sys.exit(3)
        if mode == "sleep-tree":
            child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(120)"])
            print("CHILD_PID=%d" % child.pid, flush=True)
            time.sleep(120)
        if mode == "sleep":
            time.sleep(120)
        sys.exit(0)
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "eqx-v3c-tests", Guid.NewGuid().ToString("N"));

    public EquityDocumentPipelineTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "ai"));
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "ai", "equity_pipeline.py"), FakeScript.Replace("\r\n", "\n"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>EQX_TEST_PYTHON, else the first python on PATH (skipping the WindowsApps store stub).</summary>
    internal static string? FindPython()
    {
        var env = Environment.GetEnvironmentVariable("EQX_TEST_PYTHON");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var name in new[] { "python.exe", "python3", "python" })
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    private EquityPipelineProcessSettings Settings(string python, Dictionary<string, string>? extraEnv = null, double timeoutSeconds = 60) => new(
        python, Path.Combine(_root, "ai", "equity_pipeline.py"), Path.Combine(_root, "docs"), Path.Combine(_root, "backend"),
        ConnectionString, extraEnv ?? new Dictionary<string, string>(),
        TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromMilliseconds(200));

    private sealed class Rig(ServiceProvider sp) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = sp;
        public EquityPipelineProcessRunner Runner => Provider.GetRequiredService<EquityPipelineProcessRunner>();

        public async Task<T> WithService<T>(Func<EquityDocumentPipelineService, AppDbContext, Task<T>> f)
        {
            using var scope = Provider.CreateScope();
            return await f(scope.ServiceProvider.GetRequiredService<EquityDocumentPipelineService>(),
                scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        public async Task<EquityPipelineRun> RunAsync(Guid id)
        {
            using var scope = Provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().EquityPipelineRuns.AsNoTracking().SingleAsync(r => r.Id == id);
        }

        public async ValueTask DisposeAsync() => await Provider.DisposeAsync();
    }

    private Rig NewRig(EquityPipelineProcessSettings settings, EquityDocumentsOptions? options = null)
    {
        var services = new ServiceCollection();
        var dbName = "v3c-" + Guid.NewGuid().ToString("N");
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options ?? new EquityDocumentsOptions()));
        services.AddSingleton(sp => new EquityPipelineProcessRunner(sp.GetRequiredService<IServiceScopeFactory>(), settings));
        services.AddSingleton<IEquityPipelineRunner>(sp => sp.GetRequiredService<EquityPipelineProcessRunner>());
        services.AddScoped<EquityDocumentPipelineService>();
        return new Rig(services.BuildServiceProvider());
    }

    private static async Task<EquityDocumentPipelineService.Result> Start(Rig rig, string mode = "incremental", string symbol = "BID") =>
        await rig.WithService((s, _) => s.StartAsync(symbol, mode, "admin"));

    // --- process supervision ----------------------------------------------------------------

    [Theory]
    [InlineData("exit0", "succeeded", null)]
    [InlineData("exit2", "partial", null)]
    [InlineData("exit75", "failed", "pipeline-busy")]
    [InlineData("exit3", "failed", "exit code 3")]
    public async Task ExitCode_MapsToFinalStatus(string mode, string expected, string? errorPart)
    {
        if (FindPython() is not { } python) return;
        await using var rig = NewRig(Settings(python, new() { ["FAKE_MODE"] = mode }));
        var started = await Start(rig);
        Assert.Equal(EquityDocumentPipelineService.Outcome.Started, started.Outcome);
        Assert.Equal("running", started.Run!.Status);
        await started.Completion!.WaitAsync(TimeSpan.FromSeconds(60));
        var row = await rig.RunAsync(started.Run.RunId);
        Assert.Equal(expected, row.Status);
        Assert.NotNull(row.FinishedAtUtc);
        if (errorPart is null) Assert.Null(row.Error);
        else Assert.Contains(errorPart, row.Error);
        Assert.Contains("--run-id " + started.Run.RunId.ToString("D"), row.LogTail);
        if (mode == "exit3") Assert.Contains("[stderr] boom", row.LogTail);
    }

    [Fact]
    public void FinalStatus_NeverSucceededWithoutExitZero_AndTakesTheMorePessimisticPythonStatus()
    {
        var t = TimeSpan.FromMinutes(45);
        Assert.Equal("partial", EquityPipelineProcessRunner.FinalStatus(0, EquityPipelineTermination.None, null, t, "partial", null).Status);
        Assert.Equal("failed", EquityPipelineProcessRunner.FinalStatus(0, EquityPipelineTermination.None, null, t, "failed", "x").Status);
        Assert.Equal("partial", EquityPipelineProcessRunner.FinalStatus(2, EquityPipelineTermination.None, null, t, "succeeded", null).Status);
        Assert.Equal("failed", EquityPipelineProcessRunner.FinalStatus(null, EquityPipelineTermination.None, null, t, "succeeded", null).Status);
        var timeout = EquityPipelineProcessRunner.FinalStatus(0, EquityPipelineTermination.Timeout, null, t, "succeeded", null);
        Assert.Equal("failed", timeout.Status);
        Assert.Contains("timeout after 45 min", timeout.Error);
        Assert.Equal("cancelled", EquityPipelineProcessRunner.FinalStatus(-1, EquityPipelineTermination.Cancelled, null, t, null, null).Status);
        Assert.Equal("cancelled", EquityPipelineProcessRunner.FinalStatus(-1, EquityPipelineTermination.Shutdown, null, t, null, null).Status);
        var start = EquityPipelineProcessRunner.FinalStatus(null, EquityPipelineTermination.None, "process-start-failed: x", t, null, null);
        Assert.Equal(("failed", "process-start-failed: x"), start);
        Assert.Equal("exit code 9; python: py said", EquityPipelineProcessRunner.FinalStatus(9, EquityPipelineTermination.None, null, t, null, "py said").Error);
    }

    [Fact]
    public async Task Launch_PassesDbOnlyViaEnvironment_AndArgsCarryNoSecret()
    {
        var python = FindPython() ?? "python";
        var settings = Settings(python, new()
        {
            ["FAKE_MODE"] = "env", ["OCR_TOOL"] = @"C:\Tools\tesseract.exe", ["DB_PASS"] = "override-attempt", ["EXPECT_PASS_CHECK"] = Password,
        });
        await using var rig = NewRig(settings);
        var launch = new EquityPipelineLaunch(Guid.NewGuid(), "MBB", "backfill", 12, 3, true);
        var psi = rig.Runner.BuildStartInfo(launch);
        Assert.Equal(["-I", "-X", "utf8", settings.ScriptPath, "run", "--symbol", "MBB", "--mode", "backfill", "--run-id",
            launch.RunId.ToString("D"), "--budget", "12", "--max-docs", "3", "--dry-run", "--extract-scope", "consolidated"], psi.ArgumentList.ToArray());
        Assert.Equal("all", rig.Runner.BuildStartInfo(launch with { ExtractScope = "all" }).ArgumentList[^1]);
        Assert.DoesNotContain(psi.ArgumentList, a => a.Contains(Password) || a.Contains("eqx_user") || a.Contains("Host="));
        Assert.Equal("dbhost.test", psi.Environment["DB_HOST"]);
        Assert.Equal("5544", psi.Environment["DB_PORT"]);
        Assert.Equal("eqx_user", psi.Environment["DB_USER"]);
        Assert.Equal("eqx_fake", psi.Environment["DB_NAME"]);
        Assert.Equal(Password, psi.Environment["DB_PASS"]); // reserved key: ExtraEnv cannot override it
        Assert.Equal(Path.Combine(_root, "docs"), psi.Environment["EQUITY_DOCUMENTS_ROOT"]);
        Assert.Equal(@"C:\Tools\tesseract.exe", psi.Environment["OCR_TOOL"]);
        Assert.False(psi.Environment.ContainsKey("PGPASSWORD"));
        Assert.Equal(Path.Combine(_root, "ai"), psi.WorkingDirectory);

        if (FindPython() is null) return;
        var started = await Start(rig);
        await started.Completion!.WaitAsync(TimeSpan.FromSeconds(60));
        var row = await rig.RunAsync(started.Run!.RunId);
        Assert.Equal("succeeded", row.Status);
        Assert.Contains("DB_HOST=dbhost.test", row.LogTail);
        Assert.Contains("DB_NAME=eqx_fake", row.LogTail);
        Assert.Contains("HAS_PASS=True", row.LogTail);
        Assert.Contains("PGPW_SET=False", row.LogTail);
        Assert.Contains("DOCS_ROOT=<documents-root>", row.LogTail); // absolute root scrubbed to a label
        Assert.DoesNotContain(_root, row.LogTail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LogTail_And_Error_AreScrubbed_OfSecretsAndAbsolutePaths()
    {
        if (FindPython() is not { } python) return;
        await using var rig = NewRig(Settings(python, new() { ["FAKE_MODE"] = "leak" }));
        var started = await Start(rig);
        await started.Completion!.WaitAsync(TimeSpan.FromSeconds(60));
        var row = await rig.RunAsync(started.Run!.RunId);
        Assert.Equal("failed", row.Status);
        Assert.Equal("exit code 1", row.Error);
        Assert.DoesNotContain(Password, row.LogTail);
        Assert.Contains("DB_PASS=***", row.LogTail);
        Assert.Contains("connecting with secret *** now", row.LogTail);
        Assert.DoesNotContain(_root, row.LogTail!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<ai>", row.LogTail);
        var list = await rig.WithService((s, _) => s.ListAsync("BID", 10));
        var dto = Assert.Single(list.Items);
        Assert.DoesNotContain(Password, JsonSerializer.Serialize(dto));
        Assert.False(dto.SupervisedByThisProcess);
    }

    [Fact]
    public void Scrubber_RedactsAssignmentsUrlsPathsAndJsonStrings()
    {
        var scrubber = new EquityPipelineLogScrubber(["hunter22"], [(@"D:\code\btc\ai", "<ai>")]);
        var text = scrubber.Scrub(@"password=abc pwd: 'q w' postgresql://u:p@h:5432/db hunter22 D:\code\btc\ai\x.py E:\other\dir\file.pdf https://ir.example/a:b");
        Assert.DoesNotContain("abc", text);
        Assert.DoesNotContain("'q w'", text);
        Assert.Contains("postgresql://***:***@h:5432/db", text);
        Assert.DoesNotContain("hunter22", text);
        Assert.Contains(@"<ai>\x.py", text);
        Assert.Contains("<abs>/file.pdf", text);
        Assert.Contains("https://ir.example/a:b", text);
        var json = scrubber.ScrubJson("""{"path":"E:\\data\\f.txt","n":3,"list":["hunter22",{"k":"password=zz"}]}""")!;
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("<abs>/f.txt", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("n").GetInt32());
        Assert.Equal("***", doc.RootElement.GetProperty("list")[0].GetString());
        Assert.Equal("password=***", doc.RootElement.GetProperty("list")[1].GetProperty("k").GetString());
    }

    [Fact]
    public async Task Timeout_KillsTheWholeProcessTree()
    {
        if (FindPython() is not { } python) return;
        await using var rig = NewRig(Settings(python, new() { ["FAKE_MODE"] = "sleep-tree" }, timeoutSeconds: 3));
        var sw = Stopwatch.StartNew();
        var started = await Start(rig);
        await started.Completion!.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(50));
        var row = await rig.RunAsync(started.Run!.RunId);
        Assert.Equal("failed", row.Status);
        Assert.Contains("timeout after", row.Error);
        var pidLine = row.LogTail!.Split('\n').Single(l => l.StartsWith("CHILD_PID=", StringComparison.Ordinal));
        var pid = int.Parse(pidLine["CHILD_PID=".Length..]);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        bool alive;
        do
        {
            try { using var p = Process.GetProcessById(pid); alive = !p.HasExited; }
            catch (ArgumentException) { alive = false; }
            if (alive) await Task.Delay(200);
        } while (alive && DateTime.UtcNow < deadline);
        Assert.False(alive, "grandchild process survived the timeout kill");
    }

    [Fact]
    public async Task Cancel_KillsAndRecordsCancelled_SecondStartIsBusy()
    {
        if (FindPython() is not { } python) return;
        await using var rig = NewRig(Settings(python, new() { ["FAKE_MODE"] = "sleep" }));
        var started = await Start(rig, "backfill");
        Assert.Equal(EquityDocumentPipelineService.Outcome.Started, started.Outcome);

        var busy = await Start(rig);
        Assert.Equal(EquityDocumentPipelineService.Outcome.Busy, busy.Outcome);
        Assert.Equal("PIPELINE_BUSY", busy.Code);
        Assert.Equal(started.Run!.RunId, busy.Run!.RunId);
        var other = await Start(rig, symbol: "MBB"); // another symbol is independent
        Assert.Equal(EquityDocumentPipelineService.Outcome.Started, other.Outcome);

        var cancel = await rig.WithService((s, _) => s.CancelAsync("BID", started.Run.RunId));
        Assert.Equal(EquityDocumentPipelineService.Outcome.CancelRequested, cancel.Outcome);
        Assert.True(cancel.Run!.SupervisedByThisProcess);
        await started.Completion!.WaitAsync(TimeSpan.FromSeconds(60));
        var row = await rig.RunAsync(started.Run.RunId);
        Assert.Equal("cancelled", row.Status);
        Assert.Contains("cancelled by admin", row.Error);
        Assert.Equal(EquityDocumentPipelineService.Outcome.NotRunning,
            (await rig.WithService((s, _) => s.CancelAsync("BID", started.Run.RunId))).Outcome);
        Assert.Equal(EquityDocumentPipelineService.Outcome.NotFound,
            (await rig.WithService((s, _) => s.CancelAsync("BID", Guid.NewGuid()))).Outcome);

        await rig.Runner.StopAsync(CancellationToken.None); // host shutdown kills the MBB run
        Assert.True(other.Completion!.IsCompleted);
        var mbb = await rig.RunAsync(other.Run!.RunId);
        Assert.Equal("cancelled", mbb.Status);
        Assert.Contains("host-shutdown", mbb.Error);
        Assert.Equal(EquityDocumentPipelineService.Outcome.Unavailable, (await Start(rig)).Outcome);
    }

    [Fact]
    public async Task StaleHeartbeat_IsRecovered_FreshForeignRunIsBusyAndNotOwned()
    {
        if (FindPython() is not { } python) return;
        await using var rig = NewRig(Settings(python, new() { ["FAKE_MODE"] = "exit0" }));
        var now = DateTime.UtcNow;
        var stale = new EquityPipelineRun
        {
            Id = Guid.NewGuid(), Symbol = "BID", Mode = "backfill", Trigger = "cli", Status = "running",
            StartedAtUtc = now.AddHours(-2), HeartbeatAtUtc = now.AddMinutes(-20),
        };
        var fresh = new EquityPipelineRun
        {
            Id = Guid.NewGuid(), Symbol = "MBB", Mode = "incremental", Trigger = "cli", Status = "running",
            StartedAtUtc = now.AddMinutes(-3), HeartbeatAtUtc = now.AddMinutes(-1),
        };
        await rig.WithService(async (_, db) => { db.EquityPipelineRuns.AddRange(stale, fresh); return await db.SaveChangesAsync(); });

        var started = await Start(rig);
        Assert.Equal(EquityDocumentPipelineService.Outcome.Started, started.Outcome);
        var recovered = await rig.RunAsync(stale.Id);
        Assert.Equal("failed", recovered.Status);
        Assert.StartsWith("stale-heartbeat", recovered.Error);
        Assert.NotNull(recovered.FinishedAtUtc);
        await started.Completion!.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("succeeded", (await rig.RunAsync(started.Run!.RunId)).Status);

        var busy = await Start(rig, symbol: "MBB"); // foreign (CLI) run with a fresh heartbeat
        Assert.Equal(EquityDocumentPipelineService.Outcome.Busy, busy.Outcome);
        Assert.Equal(fresh.Id, busy.Run!.RunId);
        var cancel = await rig.WithService((s, _) => s.CancelAsync("MBB", fresh.Id));
        Assert.Equal(EquityDocumentPipelineService.Outcome.NotOwned, cancel.Outcome);
        Assert.Equal("PIPELINE_RUN_NOT_OWNED", cancel.Code);
    }

    [Fact]
    public async Task ExtractScope_IsValidated_PassedAsArg_AndRecordedInCheckpoint()
    {
        if (FindPython() is not { } python) return;
        await using var rig = NewRig(Settings(python, new() { ["FAKE_MODE"] = "exit0" }));
        var bad = await rig.WithService((s, _) => s.StartAsync("BID", "incremental", "admin", extractScope: "separate"));
        Assert.Equal((EquityDocumentPipelineService.Outcome.Invalid, "INVALID_PIPELINE_REQUEST"), (bad.Outcome, bad.Code));
        var started = await rig.WithService((s, _) => s.StartAsync("BID", "backfill", "admin", extractScope: "all"));
        Assert.Equal("all", started.Run!.ExtractScope);
        await started.Completion!.WaitAsync(TimeSpan.FromSeconds(60));
        var row = await rig.RunAsync(started.Run.RunId);
        Assert.Contains("--extract-scope all", row.LogTail);
        Assert.Equal("all", EquityDocumentPipelineService.LaunchScope(row.CheckpointJson));
        var dto = Assert.Single((await rig.WithService((s, _) => s.ListAsync("BID", 5))).Items);
        Assert.Equal(("all", "succeeded"), (dto.ExtractScope, dto.Status));
        var dflt = await rig.WithService((s, _) => s.StartAsync("BID", "incremental", "admin"));
        Assert.Equal("consolidated", dflt.Run!.ExtractScope);
        await dflt.Completion!.WaitAsync(TimeSpan.FromSeconds(60));

        // Python replacing the checkpoint keeps the launch block; a foreign non-object checkpoint is left alone.
        var l = new EquityPipelineLaunch(Guid.NewGuid(), "BID", "incremental", null, null, false, "all");
        var merged = JsonDocument.Parse(EquityPipelineProcessRunner.WithLaunch("""{"docsDone":3}""", l)!).RootElement;
        Assert.Equal((3, "all"), (merged.GetProperty("docsDone").GetInt32(), merged.GetProperty("launch").GetProperty("extractScope").GetString()));
        Assert.Equal("[1]", EquityPipelineProcessRunner.WithLaunch("[1]", l));
        Assert.Equal("consolidated", new EquityDocumentsOptions().WorkerExtractScope);
    }

    [Fact]
    public async Task Start_ValidatesInput_AndReportsMissingScript()
    {
        var python = FindPython() ?? "python";
        await using var rig = NewRig(Settings(python));
        Assert.Equal(EquityDocumentPipelineService.Outcome.Invalid, (await Start(rig, "full")).Outcome);
        Assert.Equal(EquityDocumentPipelineService.Outcome.Invalid,
            (await rig.WithService((s, _) => s.StartAsync("BID", "incremental", "admin", budget: 0))).Outcome);
        await Assert.ThrowsAsync<ArgumentException>(() => Start(rig, symbol: "VCB"));
        File.Delete(Path.Combine(_root, "ai", "equity_pipeline.py"));
        var missing = await Start(rig);
        Assert.Equal(EquityDocumentPipelineService.Outcome.Unavailable, missing.Outcome);
        Assert.Contains("pipeline-script-not-found", missing.Message);
        Assert.Empty((await rig.WithService((s, _) => s.ListAsync("BID", 10))).Items);
    }

    // --- worker -----------------------------------------------------------------------------

    [Fact]
    public async Task Worker_RunsSymbolsSequentially_IsolatesFailures_AndRunsOncePerSlot()
    {
        var options = new EquityDocumentsOptions { PipelineSymbols = ["BID", "MBB", "VCB", "NOPE"] };
        var worker = new EquityDocumentPipelineWorker(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(options));
        Assert.Equal(["BID", "MBB"], worker.Symbols().ToArray());
        // 2026-10-19 10:00Z = 17:00 VN, before 18:30 ⇒ yesterday's slot (18:30 VN = 11:30Z).
        Assert.Equal(new DateTime(2026, 10, 18, 11, 30, 0, DateTimeKind.Utc),
            worker.LatestSlotUtc(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTime(2026, 10, 19, 11, 30, 0, DateTimeKind.Utc),
            worker.LatestSlotUtc(new DateTimeOffset(2026, 10, 19, 11, 30, 0, TimeSpan.Zero)));

        var disabled = new EquityDocumentPipelineWorker(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new EquityDocumentsOptions()));
        await disabled.StartAsync(CancellationToken.None);
        await disabled.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)); // PipelineEnabled=false ⇒ exits immediately

        if (FindPython() is not { } python) return;
        await using var rig = NewRig(Settings(python, new() { ["FAKE_MODE"] = "exit0", ["FAKE_FAIL_SYMBOL"] = "MBB" }), options);
        var w = new EquityDocumentPipelineWorker(rig.Provider.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Options.Options.Create(options));
        var slot = DateTime.UtcNow.AddMinutes(-1);
        var report = await w.RunOnceAsync(slot, CancellationToken.None);
        Assert.Equal((2, 1, 1), (report.Attempted, report.Succeeded, report.Failed));
        Assert.Contains("MBB=failed", report.Detail);
        var runs = await rig.WithService((_, db) => db.EquityPipelineRuns.AsNoTracking().OrderBy(r => r.StartedAtUtc).ToListAsync());
        Assert.Equal(["BID", "MBB"], runs.Select(r => r.Symbol).ToArray());
        Assert.All(runs, r => Assert.Equal(("worker", "incremental"), (r.Trigger, r.Mode)));
        Assert.True(runs[0].FinishedAtUtc <= runs[1].StartedAtUtc); // sequential
        var again = await w.RunOnceAsync(slot, CancellationToken.None);
        Assert.Equal((0, 2), (again.Attempted, again.Skipped));
    }

    // --- coverage -----------------------------------------------------------------------------

    private static readonly DateTime AsOf = new(2026, 10, 19, 3, 0, 0, DateTimeKind.Utc);

    private const string CatalogJson = """
        {"schemaVersion":"equity-metric-catalog-v1","metrics":[
          {"code":"TOTAL_ASSETS","group":"balance-sheet","statement":"balance-sheet","unit":"VND","periodTypes":["point-in-time"],"core":true},
          {"code":"PROFIT_AFTER_TAX","group":"income-statement","statement":"income-statement","unit":"VND","periodTypes":["quarter","ytd","annual","half-year"],"core":true},
          {"code":"CASA_RATIO","group":"deposits-funding","statement":"note","unit":"percent","optional":true},
          {"code":"NIM_CALC","group":"income-statement","statement":"derived","unit":"percent"}
        ]}
        """;

    private static EquitySourceDocument Doc(string period, string consolidation = "consolidated", DateTime? published = null,
        DateTime? firstSeen = null, Guid? supersedes = null, int revision = 1) => new()
    {
        Id = Guid.NewGuid(), Symbol = "BID", Entity = "BIDV", DocType = "financial-statement", Title = $"BCTC {period}",
        SourceUrl = $"https://bidv.com.vn/ir/{period}.pdf", Provider = "issuer:BIDV",
        ContentSha256 = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant().PadRight(64, '0'),
        StoragePath = $"BID/{period}.pdf", ByteSize = 10, MimeType = "application/pdf", ReportingPeriod = period,
        Consolidation = consolidation, PublishedAtUtc = published, FirstSeenAtUtc = firstSeen ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        FetchedAtUtc = firstSeen ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Revision = revision, SupersedesDocumentId = supersedes,
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static EquityDocumentExtraction Extraction(Guid doc, string status = "succeeded") => new()
    {
        Id = Guid.NewGuid(), DocumentId = doc, ExtractorVersion = "eqx-extract-v1", Method = "text-layer", Status = status,
        PageCount = 40, PagesOcr = 0, StartedAtUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        FinishedAtUtc = new DateTime(2026, 2, 1, 0, 5, 0, DateTimeKind.Utc),
    };

    private static EquityFinancialFact Fact(EquitySourceDocument d, Guid? extraction, string metric, string group, string label,
        string periodType, DateOnly end, decimal value, string status = "validated", DateTime? availableAt = null, string role = "current") => new()
    {
        Id = Guid.NewGuid(), FactKey = $"BID/{metric}/{label}/{periodType}/consolidated/{Guid.NewGuid():N}", Symbol = "BID", MetricCode = metric,
        MetricDefinitionVersion = "v1", MetricGroup = group, Statement = group == "balance-sheet" ? "balance-sheet" : "income-statement",
        PeriodLabel = label, PeriodType = periodType, PeriodEnd = end, FiscalYear = end.Year, Consolidation = "consolidated",
        Value = value, Unit = "VND", Scale = 1_000_000m, RawLabel = metric, RawValueText = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        RawUnitText = "triệu đồng", ColumnRole = role, PageNumber = 7, Location = "table 1 row 3", DocumentId = d.Id, ExtractionId = extraction,
        Status = status, IsDerived = false, AvailableAtUtc = availableAt ?? EquitySourceDocumentService.AvailableAt(d),
        CreatedAtUtc = new DateTime(2026, 2, 1, 0, 10, 0, DateTimeKind.Utc),
    };

    private static EquitySourceAttempt Attempt(long id, string period, string outcome, string? consolidation = "consolidated") => new()
    {
        Id = id, Symbol = "BID", Source = "issuer:BIDV", TargetPeriod = period, DocType = "financial-statement",
        Consolidation = consolidation, Url = outcome == "not-listed" ? null : $"https://bidv.com.vn/ir/{period}.pdf",
        AttemptedAtUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(id), Outcome = outcome,
    };

    private static EquityCoverageCell Cell(IEnumerable<EquityCoverageCell> cells, string period, string group, string consolidation = "consolidated") =>
        cells.Single(c => c.Period == period && c.MetricGroup == group && c.Consolidation == consolidation);

    [Fact]
    public void Catalog_Parse_IsTolerant_AndExplicitWhenUnavailable()
    {
        var catalog = EquityMetricCatalog.Parse(Encoding.UTF8.GetBytes(CatalogJson));
        Assert.True(catalog.IsOk);
        Assert.Equal("equity-metric-catalog-v1", catalog.Version);
        Assert.Equal(["PROFIT_AFTER_TAX", "TOTAL_ASSETS"], catalog.CoreMetrics.ToArray());
        Assert.False(catalog.Metrics.Single(m => m.Code == "CASA_RATIO").Expected);
        Assert.False(catalog.Metrics.Single(m => m.Code == "NIM_CALC").Expected);
        Assert.Equal("metric-catalog-invalid-json", EquityMetricCatalog.Parse("{oops"u8.ToArray()).Reason);
        Assert.Equal("metric-catalog-no-metrics-array", EquityMetricCatalog.Parse("\"str\""u8.ToArray()).Reason);
        Assert.Equal("metric-catalog-empty", EquityMetricCatalog.Parse("""{"metrics":[1,{"code":"X"}]}"""u8.ToArray()).Reason);
        var missing = new EquityMetricCatalogProvider(Path.Combine(_root, "nope.json")).Get();
        Assert.Equal(("catalog-unavailable", "metric-catalog-file-missing"), (missing.Status, missing.Reason));
        Assert.Equal(EquityMetricCatalog.DefaultCoreMetrics.Order(StringComparer.Ordinal).ToArray(), missing.CoreMetrics.ToArray());
    }

    [Fact]
    public void Coverage_Grid_StagesReasonsAndRefs()
    {
        var catalog = EquityMetricCatalog.Parse(Encoding.UTF8.GetBytes(CatalogJson));
        var d25q4 = Doc("2025Q4");
        var d26q1 = Doc("2026Q1");
        var d26q2 = Doc("2026Q2");
        var x26q1 = Extraction(d26q1.Id);
        var x26q2 = Extraction(d26q2.Id);
        var failed = Extraction(d25q4.Id, "failed");
        var ta = Fact(d26q2, x26q2.Id, "TOTAL_ASSETS", "balance-sheet", "2026Q2", "point-in-time", new DateOnly(2026, 6, 30), 2_900_000m);
        var patQ = Fact(d26q2, x26q2.Id, "PROFIT_AFTER_TAX", "income-statement", "2026Q2", "quarter", new DateOnly(2026, 6, 30), 7_000m, "quarantined");
        var rows = EquityFinancialFactsService.Partition([d25q4, d26q1, d26q2], [x26q1, x26q2, failed],
            [Attempt(1, "2016Q1", "not-listed"), Attempt(2, "2016Q2", "http-error"), Attempt(3, "2017Q1", "listed"), Attempt(4, "2017Q2", "too-large")],
            [ta, patQ]);

        var (first, last, cells, summary) = EquityFinancialFactsService.ComputeCoverage(rows, AsOf, catalog);
        Assert.Equal(("2016Q1", "2026Q3"), (first, last));
        Assert.DoesNotContain(cells, c => c.MetricGroup == "deposits-funding"); // only optional metrics ⇒ no cell
        Assert.Equal((43 + 11 + 10) * 2 * 2, cells.Count); // (quarters + H1 + FY) × {BS, IS} × {consolidated, separate}

        var notListed = Cell(cells, "2016Q1", "balance-sheet");
        Assert.Equal(("not-listed", "not-listed", 1L), (notListed.Stage, notListed.Reason, notListed.AttemptId));
        Assert.Equal(("not-attempted", "no-attempt"), (Cell(cells, "2016Q1", "balance-sheet", "separate").Stage, Cell(cells, "2016Q1", "balance-sheet", "separate").Reason));
        var httpErr = Cell(cells, "2016Q2", "income-statement");
        Assert.Equal(("not-attempted", "attempt-failed:http-error", 2L), (httpErr.Stage, httpErr.Reason, httpErr.AttemptId));
        Assert.Equal(("discovered", "listed-not-downloaded"), (Cell(cells, "2017Q1", "balance-sheet").Stage, Cell(cells, "2017Q1", "balance-sheet").Reason));
        Assert.Equal("too-large", Cell(cells, "2017Q2", "balance-sheet").Reason);
        var downloaded = Cell(cells, "2025Q4", "balance-sheet");
        Assert.Equal(("downloaded", "extraction-failed", d25q4.Id), (downloaded.Stage, downloaded.Reason, downloaded.DocumentId));
        var extracted = Cell(cells, "2026Q1", "income-statement");
        Assert.Equal(("extracted", "no-facts-for-group", x26q1.Id), (extracted.Stage, extracted.Reason, extracted.ExtractionId));
        Assert.Equal(["PROFIT_AFTER_TAX"], extracted.MissingMetrics.ToArray());

        var bs = Cell(cells, "2026Q2", "balance-sheet");
        Assert.Equal(("persisted", (string?)null, d26q2.Id), (bs.Stage, bs.Reason, bs.DocumentId));
        Assert.Equal((1, 1, 1), (bs.Counts.Validated, bs.Counts.ExpectedMetrics, bs.Counts.ValidatedMetrics));
        var inc = Cell(cells, "2026Q2", "income-statement");
        Assert.Equal("extracted", inc.Stage); // a quarantined fact is never validated
        Assert.Equal("facts-not-validated:extracted=0,review-needed=0,quarantined=1", inc.Reason);
        Assert.Equal((0, 1), (inc.Counts.Validated, inc.Counts.Quarantined));
        // the point-in-time balance at 2026-06-30 also serves the H1 cell
        Assert.Equal("persisted", Cell(cells, "2026H1", "balance-sheet").Stage);
        Assert.Equal("not-attempted", Cell(cells, "2026Q2", "balance-sheet", "separate").Stage);
        Assert.Equal(2, summary.ByStage["persisted"]);
        Assert.Equal(1, summary.QuarantinedFacts);

        // no catalog ⇒ all 7 groups, never "persisted", explicit reason
        var (_, _, raw, _) = EquityFinancialFactsService.ComputeCoverage(rows, AsOf, EquityMetricCatalog.Missing("metric-catalog-file-missing"));
        Assert.Equal((43 + 11 + 10) * 7 * 2, raw.Count);
        var rawBs = Cell(raw, "2026Q2", "balance-sheet");
        Assert.Equal(("validated", "catalog-unavailable"), (rawBs.Stage, rawBs.Reason));
        Assert.DoesNotContain(raw, c => c.Stage == "persisted");
    }

    [Fact]
    public async Task FactsApi_AsOfStatusAndFilters_WithDocumentRefs()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("v3c-facts-" + Guid.NewGuid()).Options);
        var published = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);
        var doc = Doc("2026Q2", published: published, firstSeen: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        // revision: same period, new bytes, issuer date unchanged ⇒ visible only from its first-seen time
        var rev = Doc("2026Q2", published: published, firstSeen: new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), supersedes: doc.Id, revision: 2);
        var x = Extraction(doc.Id);
        var xr = Extraction(rev.Id);
        db.EquitySourceDocuments.AddRange(doc, rev);
        db.EquityDocumentExtractions.AddRange(x, xr);
        var ta = Fact(doc, x.Id, "TOTAL_ASSETS", "balance-sheet", "2026Q2", "point-in-time", new DateOnly(2026, 6, 30), 100m);
        var pat = Fact(doc, x.Id, "PROFIT_AFTER_TAX", "income-statement", "2026Q2", "quarter", new DateOnly(2026, 6, 30), 5m, "quarantined");
        var old = Fact(doc, x.Id, "TOTAL_ASSETS", "balance-sheet", "2025Q2", "point-in-time", new DateOnly(2025, 6, 30), 90m, role: "comparative");
        var revFact = Fact(rev, xr.Id, "TOTAL_ASSETS", "balance-sheet", "2026Q2", "point-in-time", new DateOnly(2026, 6, 30), 101m);
        db.EquityFinancialFacts.AddRange(ta, pat, old, revFact);
        await db.SaveChangesAsync();
        Assert.Equal(rev.FirstSeenAtUtc, EquitySourceDocumentService.AvailableAt(rev));
        Assert.Equal("revision-first-seen", EquitySourceDocumentService.AvailabilityBasis(rev));
        Assert.Equal(published, EquitySourceDocumentService.AvailableAt(doc));

        var svc = new EquityFinancialFactsService(db, new EquityMetricCatalogProvider((string?)null), TimeProvider.System, null);
        async Task<EquityFinancialFactsResponse> Q(DateTime asOf, string? status = null, string? metric = null, string? from = null, string? to = null,
            string? periodType = null)
        {
            var r = await svc.GetFactsAsync("BID", new(metric, periodType, from, to, new DateTimeOffset(asOf), status, null));
            Assert.Null(r.Error);
            return r.Response!;
        }

        Assert.Empty((await Q(new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc))).Items); // before publication
        var mid = await Q(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal([old.FactKey, ta.FactKey], mid.Items.Select(i => i.FactKey).OrderBy(k => k == ta.FactKey).ToArray());
        Assert.DoesNotContain(mid.Items, i => i.FactKey == revFact.FactKey); // revision not yet first-seen
        var item = mid.Items.Single(i => i.FactKey == ta.FactKey);
        Assert.Equal((doc.SourceUrl, doc.ContentSha256, 7, "table 1 row 3"), (item.Document!.Url, item.Document.Sha256, item.Page, item.Location));
        Assert.Equal(("text-layer", "succeeded"), (item.Extraction!.Method, item.Extraction.Status));
        Assert.Equal(published, item.AvailableAtUtc);

        var later = await Q(new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc));
        Assert.Contains(later.Items, i => i.FactKey == revFact.FactKey);
        Assert.DoesNotContain(later.Items, i => i.Status == "quarantined"); // default status=validated
        Assert.Contains((await Q(AsOf, "all")).Items, i => i.FactKey == pat.FactKey);
        Assert.Single((await Q(AsOf, "quarantined")).Items);
        Assert.Equal([old.FactKey], (await Q(AsOf, to: "2025Q4")).Items.Select(i => i.FactKey));
        Assert.Equal(2, (await Q(AsOf, from: "2026-01-01")).Items.Count);
        Assert.Empty((await Q(AsOf, metric: "profit_after_tax")).Items);
        Assert.Single((await Q(AsOf, "all", metric: "profit_after_tax,NOPE")).Items);
        Assert.Empty((await Q(AsOf, periodType: "quarter")).Items);

        Assert.NotNull((await svc.GetFactsAsync("BID", new(null, null, null, null, new DateTimeOffset(AsOf), "approved", null))).Error);
        Assert.NotNull((await svc.GetFactsAsync("BID", new(null, "monthly", null, null, new DateTimeOffset(AsOf), null, null))).Error);
        Assert.NotNull((await svc.GetFactsAsync("BID", new(null, null, "2026-13-01", null, new DateTimeOffset(AsOf), null, null))).Error);

        var rows = await EquityFinancialFactsService.LoadVisibleAsync(db, "BID", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), null, CancellationToken.None);
        Assert.Equal([doc.Id], rows.Documents.Select(d => d.Id));
        var series = EquityFinancialFactsService.Series(await EquityFinancialFactsService.LoadVisibleAsync(db, "BID", AsOf, null, CancellationToken.None));
        var taSeries = series.Metrics.Single(m => m.MetricCode == "TOTAL_ASSETS");
        var q2 = taSeries.Points.Single(p => p.Period == "2026Q2");
        Assert.Equal(101m, q2.Value); // newer revision wins for the same column role
        Assert.Equal(1, series.DuplicatesDropped);
        Assert.DoesNotContain(series.Metrics, m => m.MetricCode == "PROFIT_AFTER_TAX");
    }

    [Fact]
    public async Task Series_LatestAvailableValidated_AuditedFySupersedesUnauditedQ4_AndFyCellIgnoresYtd()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("v3c-sel-" + Guid.NewGuid()).Options);
        var q4Published = new DateTime(2026, 1, 30, 0, 0, 0, DateTimeKind.Utc);
        var fyPublished = new DateTime(2026, 3, 30, 0, 0, 0, DateTimeKind.Utc);
        var q4 = Doc("2025Q4", published: q4Published, firstSeen: q4Published);
        var fy = Doc("2025", published: fyPublished, firstSeen: fyPublished);
        fy.DocType = "annual-report";
        var xq4 = Extraction(q4.Id);
        var xfy = Extraction(fy.Id);
        db.EquitySourceDocuments.AddRange(q4, fy);
        db.EquityDocumentExtractions.AddRange(xq4, xfy);
        var dec31 = new DateOnly(2025, 12, 31);
        // Balance at 31/12/2025 is labelled by the balance-date quarter (2025Q4, point-in-time) in BOTH reports.
        var taQ4 = Fact(q4, xq4.Id, "TOTAL_ASSETS", "balance-sheet", "2025Q4", "point-in-time", dec31, 100m);
        var taFy = Fact(fy, xfy.Id, "TOTAL_ASSETS", "balance-sheet", "2025Q4", "point-in-time", dec31, 101m);
        // Q4 report's 12-month column is "2025" ytd: it never competes with the audited annual "2025".
        var patYtd = Fact(q4, xq4.Id, "PROFIT_AFTER_TAX", "income-statement", "2025", "ytd", dec31, 50m, role: "cumulative-current");
        var patFy = Fact(fy, xfy.Id, "PROFIT_AFTER_TAX", "income-statement", "2025", "annual", dec31, 52m);
        db.EquityFinancialFacts.AddRange(taQ4, taFy, patYtd, patFy);
        await db.SaveChangesAsync();

        async Task<EquityFundamentalsSeriesSection> SeriesAt(DateTime asOf) =>
            EquityFinancialFactsService.Series(await EquityFinancialFactsService.LoadVisibleAsync(db, "BID", asOf, null, CancellationToken.None));
        static EquityFundamentalsSeriesPoint Point(EquityFundamentalsSeriesSection s, string metric, string label, string type) =>
            s.Metrics.Single(m => m.MetricCode == metric).Points.Single(p => p.Period == label && p.PeriodType == type);

        var before = await SeriesAt(new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal((100m, taQ4.FactKey), (Point(before, "TOTAL_ASSETS", "2025Q4", "point-in-time").Value, Point(before, "TOTAL_ASSETS", "2025Q4", "point-in-time").FactKey));
        Assert.DoesNotContain(before.Metrics.Single(m => m.MetricCode == "PROFIT_AFTER_TAX").Points, p => p.PeriodType == "annual");
        var after = await SeriesAt(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        var taAfter = Point(after, "TOTAL_ASSETS", "2025Q4", "point-in-time");
        Assert.Equal((101m, taFy.FactKey, fyPublished), (taAfter.Value, taAfter.FactKey, taAfter.AvailableAtUtc!.Value));
        Assert.Equal(50m, Point(after, "PROFIT_AFTER_TAX", "2025", "ytd").Value);
        Assert.Equal(52m, Point(after, "PROFIT_AFTER_TAX", "2025", "annual").Value);
        Assert.Equal(1, after.DuplicatesDropped);

        // GET financial-facts keeps every fact and says which one the series uses.
        var svc = new EquityFinancialFactsService(db, new EquityMetricCatalogProvider((string?)null), TimeProvider.System, null);
        var facts = (await svc.GetFactsAsync("BID", new("TOTAL_ASSETS", null, null, null,
            new DateTimeOffset(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)), null, null))).Response!;
        Assert.Equal(2, facts.Count);
        Assert.Equal((true, "latest-available-validated"), (facts.Items.Single(i => i.FactKey == taFy.FactKey).Selected, facts.Items.Single(i => i.FactKey == taFy.FactKey).Selection));
        Assert.Equal((false, "superseded-by-later-available-validated"), (facts.Items.Single(i => i.FactKey == taQ4.FactKey).Selected, facts.Items.Single(i => i.FactKey == taQ4.FactKey).Selection));
        var early = (await svc.GetFactsAsync("BID", new("TOTAL_ASSETS", null, null, null,
            new DateTimeOffset(new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc)), "all", null))).Response!;
        Assert.True(Assert.Single(early.Items).Selected);

        // Readiness reads the same selection: the core-quarters check sees the audited value's fact.
        // Coverage: the FY cell counts the annual fact only; the Q4 cell counts the point-in-time balance.
        var catalog = EquityMetricCatalog.Parse(Encoding.UTF8.GetBytes(CatalogJson));
        var rowsBefore = await EquityFinancialFactsService.LoadVisibleAsync(db, "BID", new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc), null, CancellationToken.None);
        var (_, _, cellsBefore, _) = EquityFinancialFactsService.ComputeCoverage(rowsBefore, AsOf, catalog);
        Assert.Equal(0, Cell(cellsBefore, "2025", "income-statement").Counts.Facts); // ytd "2025" is not FY
        Assert.Equal("persisted", Cell(cellsBefore, "2025Q4", "balance-sheet").Stage);
        var rowsAfter = await EquityFinancialFactsService.LoadVisibleAsync(db, "BID", AsOf, null, CancellationToken.None);
        var (_, _, cellsAfter, _) = EquityFinancialFactsService.ComputeCoverage(rowsAfter, AsOf, catalog);
        var fyIs = Cell(cellsAfter, "2025", "income-statement");
        Assert.Equal(("persisted", 1, fy.Id), (fyIs.Stage, fyIs.Counts.Facts, fyIs.DocumentId!.Value));
        Assert.Equal(0, Cell(cellsAfter, "2025Q4", "income-statement").Counts.Facts); // no quarter-type fact for Q4 here
    }

    [Fact]
    public async Task ReExtraction_V2SupersedesV1_ButAFailedV2KeepsV1()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("v3c-reext-" + Guid.NewGuid()).Options);
        var published = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);
        var reDoc = Doc("2026Q2", published: published, firstSeen: published);
        var keepDoc = Doc("2026Q1", published: published, firstSeen: published);
        EquityDocumentExtraction Ext(Guid doc, string version, string status, int day)
        {
            var e = Extraction(doc, status);
            e.ExtractorVersion = version;
            e.StartedAtUtc = new DateTime(2026, 9, day, 0, 0, 0, DateTimeKind.Utc);
            e.FinishedAtUtc = e.StartedAtUtc.AddMinutes(5);
            return e;
        }
        var v1 = Ext(reDoc.Id, "eqx-facts-v1", "succeeded", 1);
        var v2 = Ext(reDoc.Id, "eqx-facts-v2", "partial", 20);
        var keepV1 = Ext(keepDoc.Id, "eqx-facts-v1", "succeeded", 1);
        var keepV2 = Ext(keepDoc.Id, "eqx-facts-v2", "failed", 20);
        db.EquitySourceDocuments.AddRange(reDoc, keepDoc);
        db.EquityDocumentExtractions.AddRange(v1, v2, keepV1, keepV2);
        var jun30 = new DateOnly(2026, 6, 30);
        var mar31 = new DateOnly(2026, 3, 31);
        var taV1 = Fact(reDoc, v1.Id, "TOTAL_ASSETS", "balance-sheet", "2026Q2", "point-in-time", jun30, 100m);
        var taV2 = Fact(reDoc, v2.Id, "TOTAL_ASSETS", "balance-sheet", "2026Q2", "point-in-time", jun30, 105m);
        var patV1 = Fact(reDoc, v1.Id, "PROFIT_AFTER_TAX", "income-statement", "2026Q2", "quarter", jun30, 9m);
        var derivedV1 = Fact(reDoc, null, "NPL_RATIO_CALC", "credit-quality", "2026Q2", "point-in-time", jun30, 1.2m);
        derivedV1.IsDerived = true;
        derivedV1.ColumnRole = "derived";
        derivedV1.PageNumber = null;
        derivedV1.DerivationJson = $$"""{"formula":"a/b","inputs":["{{taV1.FactKey}}"],"derivationVersion":"d1"}""";
        var keepTa = Fact(keepDoc, keepV1.Id, "TOTAL_ASSETS", "balance-sheet", "2026Q1", "point-in-time", mar31, 90m);
        db.EquityFinancialFacts.AddRange(taV1, taV2, patV1, derivedV1, keepTa);
        await db.SaveChangesAsync();

        var rows = await EquityFinancialFactsService.LoadVisibleAsync(db, "BID", AsOf, null, CancellationToken.None);
        Assert.Equal(new[] { keepTa.FactKey, taV2.FactKey }.Order(StringComparer.Ordinal).ToArray(),
            rows.Facts.Select(f => f.FactKey).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(3, rows.Superseded.Count); // v1 balance, v1 income, derived-from-v1

        var series = EquityFinancialFactsService.Series(rows);
        var ta = series.Metrics.Single(m => m.MetricCode == "TOTAL_ASSETS").Points;
        Assert.Equal(105m, ta.Single(p => p.Period == "2026Q2").Value); // v2 wins
        Assert.Equal(90m, ta.Single(p => p.Period == "2026Q1").Value);  // v2 failed ⇒ v1 still counts
        Assert.DoesNotContain(series.Metrics, m => m.MetricCode is "PROFIT_AFTER_TAX" or "NPL_RATIO_CALC");
        Assert.Equal(0, series.DuplicatesDropped);

        var catalog = EquityMetricCatalog.Parse(Encoding.UTF8.GetBytes(CatalogJson));
        var (_, _, cells, summary) = EquityFinancialFactsService.ComputeCoverage(rows, AsOf, catalog);
        var bs = Cell(cells, "2026Q2", "balance-sheet");
        Assert.Equal(("persisted", 1, "eqx-facts-v2", v2.Id), (bs.Stage, bs.Counts.Facts, bs.ExtractorVersion, bs.ExtractionId!.Value));
        var inc = Cell(cells, "2026Q2", "income-statement");
        Assert.Equal(("extracted", "no-facts-for-group", 0), (inc.Stage, inc.Reason, inc.Counts.Facts)); // v1 income fact not counted
        var keep = Cell(cells, "2026Q1", "balance-sheet");
        Assert.Equal(("persisted", "eqx-facts-v1", keepV1.Id), (keep.Stage, keep.ExtractorVersion, keep.ExtractionId!.Value));
        Assert.Equal(2, summary.Facts);
        Assert.Equal(("partial", "eqx-facts-v2", 1), (EquityFinancialFactsService.ExtractionStatus(rows, reDoc.Id).Status,
            EquityFinancialFactsService.ExtractionStatus(rows, reDoc.Id).ExtractorVersion!, EquityFinancialFactsService.ExtractionStatus(rows, reDoc.Id).Facts));
        Assert.Equal("eqx-facts-v1", EquityFinancialFactsService.ExtractionStatus(rows, keepDoc.Id).ExtractorVersion);

        // Extraction time is processing time: an asOf before v2 finished still reads v2 (retrospective, current status).
        var early = await EquityFinancialFactsService.LoadVisibleAsync(db, "BID", new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc), null, CancellationToken.None);
        Assert.Equal(105m, EquityFinancialFactsService.Series(early).Metrics.Single(m => m.MetricCode == "TOTAL_ASSETS").Points.Single(p => p.Period == "2026Q2").Value);

        var svc = new EquityFinancialFactsService(db, new EquityMetricCatalogProvider((string?)null), TimeProvider.System, null);
        var all = (await svc.GetFactsAsync("BID", new(null, null, null, null, new DateTimeOffset(AsOf), "all", null))).Response!;
        Assert.Equal(5, all.Count); // history stays listed
        foreach (var key in new[] { taV1.FactKey, patV1.FactKey, derivedV1.FactKey })
            Assert.Equal((false, "superseded-extraction"), (all.Items.Single(i => i.FactKey == key).Selected, all.Items.Single(i => i.FactKey == key).Selection));
        Assert.Equal((true, "latest-available-validated"), (all.Items.Single(i => i.FactKey == taV2.FactKey).Selected, all.Items.Single(i => i.FactKey == taV2.FactKey).Selection));
        Assert.True(all.Items.Single(i => i.FactKey == keepTa.FactKey).Selected);
    }

    [Fact]
    public async Task SeriesAndFacts_ExposeScaleAndExactNormalizedValue_ForTrieuDongFacts()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("v3c-scale-" + Guid.NewGuid()).Options);
        var doc = Doc("2026Q2", published: new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc));
        var x = Extraction(doc.Id);
        var fact = Fact(doc, x.Id, "TOTAL_ASSETS", "balance-sheet", "2026Q2", "point-in-time", new DateOnly(2026, 6, 30), 1_854_649.25m);
        Assert.Equal(1_000_000m, fact.Scale); // triệu đồng
        db.EquitySourceDocuments.Add(doc);
        db.EquityDocumentExtractions.Add(x);
        db.EquityFinancialFacts.Add(fact);
        await db.SaveChangesAsync();

        var point = EquityFinancialFactsService.Series(await EquityFinancialFactsService.LoadVisibleAsync(db, "BID", AsOf, null, CancellationToken.None))
            .Metrics.Single().Points.Single();
        Assert.Equal((1_854_649.25m, 1_000_000m, 1_854_649_250_000m), (point.Value, point.Scale!.Value, point.NormalizedValue!.Value));
        var svc = new EquityFinancialFactsService(db, new EquityMetricCatalogProvider((string?)null), TimeProvider.System, null);
        var response = (await svc.GetFactsAsync("BID", new(null, null, null, null, new DateTimeOffset(AsOf), null, null))).Response!;
        var item = Assert.Single(response.Items);
        Assert.Equal((1_854_649.25m, 1_000_000m, 1_854_649_250_000m), (item.Value, item.Scale, item.NormalizedValue!.Value));
        Assert.Contains(response.Rules, r => r.StartsWith("normalizedValue is in unit", StringComparison.Ordinal));
        var json = JsonSerializer.Serialize(point, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"normalizedValue\":1854649250000", json.Replace(".00", ""));
        Assert.Contains("\"scale\":1000000", json);
    }

    [Fact]
    public async Task PastAsOf_DocumentsPublishedLongBeforeExtraction_AreVisible_LaterOnesNotYetPublished()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("v3c-pastasof-" + Guid.NewGuid()).Options);
        var extractedAt = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc); // processing time, long after publication
        var q1 = Doc("2024Q1", published: new DateTime(2024, 4, 27, 0, 0, 0, DateTimeKind.Utc), firstSeen: extractedAt.AddDays(-1));
        var q4late = Doc("2023Q4", published: new DateTime(2024, 8, 1, 0, 0, 0, DateTimeKind.Utc), firstSeen: extractedAt.AddDays(-1));
        Assert.Equal(new DateTime(2024, 4, 27, 0, 0, 0, DateTimeKind.Utc), EquitySourceDocumentService.AvailableAt(q1)); // issuer date, not first-seen
        var x1 = Extraction(q1.Id);
        var x4 = Extraction(q4late.Id);
        foreach (var x in new[] { x1, x4 }) { x.StartedAtUtc = extractedAt; x.FinishedAtUtc = extractedAt.AddMinutes(3); x.ExtractorVersion = "eqx-facts-v2"; }
        db.EquitySourceDocuments.AddRange(q1, q4late);
        db.EquityDocumentExtractions.AddRange(x1, x4);
        var ta = Fact(q1, x1.Id, "TOTAL_ASSETS", "balance-sheet", "2024Q1", "point-in-time", new DateOnly(2024, 3, 31), 2_500_000m);
        var pat = Fact(q1, x1.Id, "PROFIT_AFTER_TAX", "income-statement", "2024Q1", "quarter", new DateOnly(2024, 3, 31), 7_000m);
        var ta4 = Fact(q4late, x4.Id, "TOTAL_ASSETS", "balance-sheet", "2023Q4", "point-in-time", new DateOnly(2023, 12, 31), 2_400_000m);
        db.EquityFinancialFacts.AddRange(ta, pat, ta4);
        db.EquitySourceAttempts.Add(Attempt(1, "2023Q4", "downloaded"));
        await db.SaveChangesAsync();
        Assert.Equal(q1.PublishedAtUtc, ta.AvailableAtUtc);

        var asOf = new DateTimeOffset(2024, 6, 30, 23, 59, 0, TimeSpan.FromHours(7)).UtcDateTime;
        var rows = await EquityFinancialFactsService.LoadVisibleAsync(db, "BID", asOf, null, CancellationToken.None);
        var series = EquityFinancialFactsService.Series(rows);
        Assert.Equal(("ok", 2), (series.Status, series.FactCount));
        Assert.Contains(series.Rules, r => r == EquityFinancialFactsService.RetrospectiveRule);
        Assert.Equal(("succeeded", "eqx-facts-v2", 2), (EquityFinancialFactsService.ExtractionStatus(rows, q1.Id).Status,
            EquityFinancialFactsService.ExtractionStatus(rows, q1.Id).ExtractorVersion!, EquityFinancialFactsService.ExtractionStatus(rows, q1.Id).ValidatedFacts));

        var catalog = EquityMetricCatalog.Parse(Encoding.UTF8.GetBytes(CatalogJson));
        var (_, last, cells, summary) = EquityFinancialFactsService.ComputeCoverage(rows, asOf, catalog);
        Assert.Equal("2024Q1", last);
        Assert.Equal(("persisted", "eqx-facts-v2"), (Cell(cells, "2024Q1", "balance-sheet").Stage, Cell(cells, "2024Q1", "balance-sheet").ExtractorVersion));
        Assert.Equal("persisted", Cell(cells, "2024Q1", "income-statement").Stage);
        var late = Cell(cells, "2023Q4", "balance-sheet");
        // Published (and the 2026 "downloaded" attempt made) after asOf: nothing about it exists at asOf.
        Assert.Equal(("not-attempted", "no-attempt", (long?)null), (late.Stage, late.Reason, late.AttemptId));
        Assert.Equal(2, summary.ValidatedFacts);
        Assert.Contains(EquityFinancialFactsService.CoverageRules, r => r == EquityFinancialFactsService.RetrospectiveRule);

        var svc = new EquityFinancialFactsService(db, new EquityMetricCatalogProvider((string?)null), TimeProvider.System, null);
        var facts = (await svc.GetFactsAsync("BID", new(null, null, null, null, new DateTimeOffset(asOf), null, null))).Response!;
        Assert.Equal(2, facts.Count);
        Assert.All(facts.Items, i => Assert.True(i.Selected));
        Assert.Equal(3, (await svc.GetFactsAsync("BID", new(null, null, null, null, DateTimeOffset.UtcNow, null, null))).Response!.Count);
    }

    [Fact]
    public void Coverage_ComparativeOnlyCells_AreExplicit_AndSeriesPointsCarryColumnRole()
    {
        var catalog = EquityMetricCatalog.Parse(Encoding.UTF8.GetBytes(CatalogJson));
        // FY2017 has no stored report; the FY2018 annual report prints 2017 as its comparative column.
        var fy18 = Doc("2018", published: new DateTime(2019, 3, 30, 0, 0, 0, DateTimeKind.Utc));
        fy18.DocType = "annual-report";
        var x18 = Extraction(fy18.Id);
        var dec17 = new DateOnly(2017, 12, 31);
        var pat17 = Fact(fy18, x18.Id, "PROFIT_AFTER_TAX", "income-statement", "2017", "annual", dec17, 4_000m, role: "comparative");
        var ta17 = Fact(fy18, x18.Id, "TOTAL_ASSETS", "balance-sheet", "2017Q4", "point-in-time", dec17, 300_000m, role: "comparative");
        var pat18 = Fact(fy18, x18.Id, "PROFIT_AFTER_TAX", "income-statement", "2018", "annual", new DateOnly(2018, 12, 31), 5_000m);
        var rows = EquityFinancialFactsService.Partition([fy18], [x18], [], [pat17, ta17, pat18]);
        var (_, _, cells, summary) = EquityFinancialFactsService.ComputeCoverage(rows, AsOf, catalog);

        var fy17 = Cell(cells, "2017", "income-statement");
        Assert.Equal(("persisted", "comparative-only", fy18.Id), (fy17.Stage, fy17.Source, fy17.DocumentId!.Value));
        Assert.Equal([fy18.Id], fy17.SourceDocumentIds!.ToArray());
        Assert.Equal("comparative-only", Cell(cells, "2017Q4", "balance-sheet").Source);
        var own = Cell(cells, "2018", "income-statement");
        Assert.Equal(("persisted", "own-report"), (own.Stage, own.Source));
        Assert.Null(own.SourceDocumentIds);
        Assert.Null(Cell(cells, "2016", "income-statement").Source);
        // 2017 IS + the 2017Q4 balance and the FY2017 balance cell (same point-in-time balance at 31/12/2017)
        Assert.Equal(3, summary.ComparativeOnlyCells);
        Assert.Equal(3, summary.ComparativeOnlyByStage!["persisted"]);

        var series = EquityFinancialFactsService.Series(rows);
        var points = series.Metrics.Single(m => m.MetricCode == "PROFIT_AFTER_TAX").Points;
        Assert.Equal(("comparative", "current"), (points.Single(p => p.Period == "2017").ColumnRole!, points.Single(p => p.Period == "2018").ColumnRole!));
        var gaps = EquityFinancialFactsService.CoverageSection(rows, AsOf, catalog);
        Assert.Equal(3, gaps.Summary.ComparativeOnlyCells);
        Assert.Contains(gaps.Rules, r => r.StartsWith("source: own-report", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Supersession_AuditedAnnualReplacesQ4TwelveMonthYtd_OnceAvailable()
    {
        // BID 2020 shape: Q4 report's 12-month column ("2020", ytd, 7,362,643) superseded by the audited annual ("2020", annual,
        // 7,223,565, available 2021-03-06) via ValidationJson.crossDocument.supersededBy.
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("v3c-sup-" + Guid.NewGuid()).Options);
        var q4 = Doc("2020Q4", published: new DateTime(2021, 1, 30, 0, 0, 0, DateTimeKind.Utc));
        var fy = Doc("2020", published: new DateTime(2021, 3, 6, 0, 0, 0, DateTimeKind.Utc));
        fy.DocType = "annual-report";
        var xq = Extraction(q4.Id);
        var xf = Extraction(fy.Id);
        var dec31 = new DateOnly(2020, 12, 31);
        var annual = Fact(fy, xf.Id, "PROFIT_AFTER_TAX", "income-statement", "2020", "annual", dec31, 7_223_565m);
        var ytd = Fact(q4, xq.Id, "PROFIT_AFTER_TAX", "income-statement", "2020", "ytd", dec31, 7_362_643m, role: "cumulative-current");
        ytd.ValidationJson = $$$"""{"extractionStatus":"validated","crossDocument":{"supersededBy":["{{{annual.FactKey}}}"],"supersessionReason":"audited-annual"}}""";
        // a quarantined "superseder" never replaces anything
        var bogus = Fact(fy, xf.Id, "PROFIT_BEFORE_TAX", "income-statement", "2020", "annual", dec31, 1m, "quarantined");
        var pbt = Fact(q4, xq.Id, "PROFIT_BEFORE_TAX", "income-statement", "2020", "ytd", dec31, 9_000_000m, role: "cumulative-current");
        pbt.ValidationJson = $$"""{"supersededBy":"{{bogus.FactKey}}","supersessionReason":"x"}""";
        db.EquitySourceDocuments.AddRange(q4, fy);
        db.EquityDocumentExtractions.AddRange(xq, xf);
        db.EquityFinancialFacts.AddRange(annual, ytd, bogus, pbt);
        await db.SaveChangesAsync();

        async Task<EquityFundamentalsSeriesPoint> Ytd(DateTime asOf, string metric = "PROFIT_AFTER_TAX") =>
            EquityFinancialFactsService.Series(await EquityFinancialFactsService.LoadVisibleAsync(db, "BID", asOf, null, CancellationToken.None))
                .Metrics.Single(m => m.MetricCode == metric).Points.Single(p => p.Period == "2020" && p.PeriodType == "ytd");

        var before = await Ytd(new DateTime(2021, 2, 15, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal((7_362_643m, ytd.FactKey, "latest-available-validated"), (before.Value, before.FactKey, before.Selection!));
        Assert.Null(before.SupersededFactKey);

        var asOf = new DateTime(2021, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var after = await Ytd(asOf);
        Assert.Equal((7_223_565m, annual.FactKey, "superseded-by-later-report", ytd.FactKey, "audited-annual", "annual"),
            (after.Value, after.FactKey, after.Selection!, after.SupersededFactKey!, after.SupersessionReason!, after.SourcePeriodType!));
        Assert.Equal([annual.FactKey], after.SupersededBy!.ToArray());
        var series = EquityFinancialFactsService.Series(await EquityFinancialFactsService.LoadVisibleAsync(db, "BID", asOf, null, CancellationToken.None));
        Assert.Equal(7_223_565m, series.Metrics.Single(m => m.MetricCode == "PROFIT_AFTER_TAX").Points.Single(p => p.PeriodType == "annual").Value);
        Assert.Equal(9_000_000m, (await Ytd(asOf, "PROFIT_BEFORE_TAX")).Value); // quarantined superseder is ignored

        var svc = new EquityFinancialFactsService(db, new EquityMetricCatalogProvider((string?)null), TimeProvider.System, null);
        var facts = (await svc.GetFactsAsync("BID", new("PROFIT_AFTER_TAX", null, null, null, new DateTimeOffset(asOf), null, null))).Response!;
        Assert.Equal(2, facts.Count); // the original stays listed
        var orig = facts.Items.Single(i => i.FactKey == ytd.FactKey);
        Assert.Equal((false, "superseded-by-later-report"), (orig.Selected, orig.Selection));
        Assert.Equal((true, "latest-available-validated"), (facts.Items.Single(i => i.FactKey == annual.FactKey).Selected,
            facts.Items.Single(i => i.FactKey == annual.FactKey).Selection));

        var links = EquityFinancialFactsService.SupersessionOf(
            """{"supersededBy":["a"],"supersessionReason":"r1","crossDocument":{"supersededBy":"b"},"supersededByDerived":{"factKeys":["c","a"],"reason":"r3"}}""");
        Assert.Equal(["a", "b", "c"], links.Keys);
        Assert.Equal("r1", links.Reason);
        Assert.Equal("r3", EquityFinancialFactsService.SupersessionOf("""{"supersededByDerived":{"factKeys":["c"],"reason":"r3"}}""").Reason);
        Assert.Empty(EquityFinancialFactsService.SupersessionOf("""[1]""").Keys);
        Assert.Empty(EquityFinancialFactsService.SupersessionOf("""{"crossDocument":"x","supersededBy":5}""").Keys);
    }

    [Fact]
    public void Selection_TieOnAvailability_PrefersTheNewerExtraction()
    {
        var a = Doc("2026Q2");
        var b = Doc("2026Q2");
        var xa = Extraction(a.Id);
        var xb = Extraction(b.Id);
        xb.FinishedAtUtc = xa.FinishedAtUtc.AddDays(3);
        var end = new DateOnly(2026, 6, 30);
        var at = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);
        var fa = Fact(a, xa.Id, "TOTAL_ASSETS", "balance-sheet", "2026Q2", "point-in-time", end, 1m, availableAt: at);
        var fb = Fact(b, xb.Id, "TOTAL_ASSETS", "balance-sheet", "2026Q2", "point-in-time", end, 2m, availableAt: at);
        var rows = EquityFinancialFactsService.Partition([a, b], [xa, xb], [], [fa, fb]);
        Assert.Equal(fb.FactKey, Assert.Single(EquityFinancialFactsService.SelectSeries(rows).Values).Fact.FactKey);
        Assert.Equal(["fk1"], EquityFinancialFactsService.DerivationInputs("""{"inputs":["fk1",3]}"""));
        Assert.Empty(EquityFinancialFactsService.DerivationInputs("""["x"]"""));
        Assert.Empty(EquityFinancialFactsService.DerivationInputs("""{"inputs":"x"}"""));
    }

    [Fact]
    public void Coverage_CashFlowHasNoQuarterCells_BecauseTheCatalogPrintsItCumulativeOnly()
    {
        var catalog = EquityMetricCatalog.Parse(Encoding.UTF8.GetBytes("""
            {"metrics":[{"code":"NET_CASH_FROM_OPERATING","group":"cash-flow","periodTypes":["ytd","half-year","annual"]},
                        {"code":"TOTAL_ASSETS","group":"balance-sheet","periodTypes":["point-in-time"]}]}
            """));
        var (_, _, cells, _) = EquityFinancialFactsService.ComputeCoverage(EquityFinancialFactsService.Partition([], [], [], []), AsOf, catalog);
        Assert.DoesNotContain(cells, c => c.MetricGroup == "cash-flow" && c.PeriodKind == "quarter");
        Assert.Contains(cells, c => c.MetricGroup == "cash-flow" && c.PeriodKind == "annual");
        Assert.Contains(cells, c => c.MetricGroup == "cash-flow" && c.PeriodKind == "half-year");
        Assert.Contains(EquityFinancialFactsService.CoverageRules, r => r.Contains("cash-flow metrics are printed cumulative only"));
    }

    [Fact]
    public async Task RealMetricCatalog_CoreFlags_ReachReadinessThroughDi()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "ai", "equity_facts", "metric_catalog.json"));
        if (!File.Exists(path)) return; // ai repo not checked out next to backend
        var catalog = new EquityMetricCatalogProvider(path).Get();
        Assert.True(catalog.IsOk, catalog.Reason);
        Assert.True(catalog.CoreFromCatalog);
        Assert.Equal(["CUSTOMER_DEPOSITS", "CUSTOMER_LOANS_GROSS", "NET_INTEREST_INCOME", "PROFIT_AFTER_TAX", "PROFIT_AFTER_TAX_PARENT",
            "PROFIT_BEFORE_TAX", "TOTAL_ASSETS", "TOTAL_EQUITY"], catalog.CoreMetrics.ToArray());
        Assert.False(catalog.Metrics.Single(m => m.Code == "EQUITY_ATTRIBUTABLE_PARENT").Expected); // derived
        Assert.False(catalog.Metrics.Single(m => m.Code == "CAR").Expected); // optional

        await using var factory = new PipelineFactory(FindPython() ?? "python", _root, catalogPath: path);
        var snapshot = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/api/equities/BID/research-snapshot")).RootElement;
        var info = snapshot.GetProperty("fundamentalsCoverage").GetProperty("catalog");
        Assert.Equal(("ok", true, "eqx-metric-catalog-v1"), (info.GetProperty("status").GetString(), info.GetProperty("coreMetricsFromCatalog").GetBoolean(),
            info.GetProperty("version").GetString()));
        Assert.Contains("PROFIT_AFTER_TAX_PARENT", info.GetProperty("coreMetrics").EnumerateArray().Select(e => e.GetString()));
    }

    // --- HTTP ---------------------------------------------------------------------------------

    private sealed class PipelineFactory(string python, string root, string? catalogPath = null) : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = ConnectionString,
                ["EquityDocuments:PythonPath"] = python,
                ["EquityDocuments:PipelineScript"] = Path.Combine(root, "ai", "equity_pipeline.py"),
                ["EquityDocuments:RootPath"] = Path.Combine(root, "docs"),
                ["EquityDocuments:ExtraEnv:FAKE_MODE"] = "exit0",
                ["EquityFacts:MetricCatalogPath"] = catalogPath ?? Path.Combine(root, "missing-catalog.json"),
            }));
            builder.ConfigureServices(services =>
            {
                foreach (var d in services.Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)).ToList()) services.Remove(d);
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("v3c-http-" + (catalogPath is null ? "a" : "b")));
            });
        }
    }

    [Fact]
    public async Task Http_AdminGuard_202_409_PublicReads()
    {
        var python = FindPython();
        await using var factory = new PipelineFactory(python ?? "python", _root);
        var client = factory.CreateClient();
        async Task AssertCode(HttpResponseMessage response, HttpStatusCode status, string code)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(code, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/equities/BID/admin/documents/pipeline?mode=incremental", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsync($"/api/equities/BID/admin/documents/pipeline/{Guid.NewGuid()}/cancel", null)).StatusCode);

        var coverage = await client.GetAsync("/api/equities/BID/fundamentals/coverage");
        Assert.Equal(HttpStatusCode.OK, coverage.StatusCode);
        var cov = JsonDocument.Parse(await coverage.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("equity-fundamentals-coverage-v1", cov.GetProperty("contractVersion").GetString());
        Assert.Equal("catalog-unavailable", cov.GetProperty("catalog").GetProperty("status").GetString());
        Assert.Equal("2016Q1", cov.GetProperty("firstPeriod").GetString());
        var facts = await client.GetAsync("/api/equities/MBB/financial-facts?metric=TOTAL_ASSETS&asOf=2026-10-01T00:00:00Z");
        Assert.Equal(HttpStatusCode.OK, facts.StatusCode);
        Assert.Equal(0, JsonDocument.Parse(await facts.Content.ReadAsStringAsync()).RootElement.GetProperty("count").GetInt32());
        await AssertCode(await client.GetAsync("/api/equities/BID/financial-facts?status=nope"), HttpStatusCode.BadRequest, "INVALID_FACTS_QUERY");
        await AssertCode(await client.GetAsync("/api/equities/BID/financial-facts?asOf=2026-10-01"), HttpStatusCode.BadRequest, "INVALID_AS_OF");
        await AssertCode(await client.GetAsync("/api/equities/VCB/fundamentals/coverage"), HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");
        await AssertCode(await client.GetAsync("/api/equities/NOPE/documents/pipeline-runs"), HttpStatusCode.NotFound, "UNKNOWN_SYMBOL");

        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", TestWebApplicationFactory.AdminApiKey);
        await AssertCode(await admin.PostAsync("/api/equities/BID/admin/documents/pipeline?mode=full", null),
            HttpStatusCode.BadRequest, "INVALID_PIPELINE_REQUEST");
        await AssertCode(await admin.PostAsync($"/api/equities/BID/admin/documents/pipeline/{Guid.NewGuid()}/cancel", null),
            HttpStatusCode.NotFound, "PIPELINE_RUN_NOT_FOUND");
        if (python is null) return;

        // a foreign running row (fresh heartbeat) ⇒ 409 PIPELINE_BUSY
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.EquityPipelineRuns.Add(new EquityPipelineRun
            {
                Id = Guid.NewGuid(), Symbol = "MBB", Mode = "backfill", Trigger = "cli", Status = "running",
                StartedAtUtc = DateTime.UtcNow, HeartbeatAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        await AssertCode(await admin.PostAsync("/api/equities/MBB/admin/documents/pipeline?mode=incremental", null),
            HttpStatusCode.Conflict, "PIPELINE_BUSY");

        var accepted = await admin.PostAsync("/api/equities/BID/admin/documents/pipeline?mode=incremental&budget=5", null);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var run = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("running", run.GetProperty("status").GetString());
        var runId = run.GetProperty("runId").GetString();
        string? status = null;
        for (var i = 0; i < 100 && status is null or "running"; i++)
        {
            await Task.Delay(200);
            var list = JsonDocument.Parse(await client.GetStringAsync("/api/equities/BID/documents/pipeline-runs?take=5")).RootElement;
            status = list.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("runId").GetString() == runId).GetProperty("status").GetString();
        }
        Assert.Equal("succeeded", status);
        var body = await client.GetStringAsync("/api/equities/BID/documents/pipeline-runs");
        Assert.DoesNotContain(Password, body);
        Assert.DoesNotContain(_root.Replace("\\", "\\\\"), body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--budget 5", body);
    }
}
