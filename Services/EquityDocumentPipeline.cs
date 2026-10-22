using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Backend.Services;

// EQX v3 §1/§4 — backend orchestration of ai/equity_pipeline.py. The Python process does the work and
// writes PostgreSQL directly (heartbeat/counts/checkpoint on the EquityPipelineRuns row this backend
// created); the backend owns run creation, the one-running-per-symbol lock, timeout/cancel (process-tree
// kill), the scrubbed log tail and the final status (never "succeeded" unless the process exited 0).

public static class EquityPipelineStatuses
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public static class EquityPipelineModes
{
    public const string Backfill = "backfill";
    public const string Incremental = "incremental";
}

/// <summary>Exit-code contract of <c>ai/equity_pipeline.py run</c>.</summary>
public static class EquityPipelineExitCodes
{
    public const int Succeeded = 0;
    public const int Partial = 2;
    public const int Busy = 75;
}

public enum EquityPipelineTermination { None, Timeout, Cancelled, Shutdown }

public sealed record EquityPipelineLaunch(Guid RunId, string Symbol, string Mode, int? Budget, int? MaxDocs, bool DryRun,
    string ExtractScope = EquityPipelineExtractScopes.Consolidated)
{
    /// <summary>Launch parameters recorded under CheckpointJson.launch (re-added at finalization if the Python side replaced it).</summary>
    public JsonObject LaunchJson() => new()
    {
        ["extractScope"] = ExtractScope, ["budget"] = Budget, ["maxDocs"] = MaxDocs, ["dryRun"] = DryRun,
    };
}

/// <summary><c>--extract-scope</c> of ai/equity_pipeline.py.</summary>
public static class EquityPipelineExtractScopes
{
    public const string Consolidated = "consolidated";
    public const string All = "all";

    public static bool IsValid(string? scope) => scope is Consolidated or All;
}

/// <summary>Supervises pipeline subprocesses in the background (singleton; never inside an HTTP request).</summary>
public interface IEquityPipelineRunner
{
    /// <summary>Null when a run can be launched; else a short reason (python-not-found, pipeline-script-not-found...).</summary>
    string? Unavailable();

    /// <summary>Starts supervising the run; the returned task completes after the final status is written.</summary>
    Task Launch(EquityPipelineLaunch launch);

    /// <summary>Requests cancellation (process-tree kill). False when this process does not supervise the run.</summary>
    bool TryCancel(Guid runId);

    bool IsActive(Guid runId);
    bool IsSymbolActive(string symbol);

    /// <summary>Removes secrets and absolute paths from text that leaves the backend.</summary>
    string Scrub(string text);

    /// <summary>Scrubs every string value of a JSON document (null when unparseable).</summary>
    string? ScrubJson(string? json);
}

/// <summary>Resolved launch settings (absolute paths, connection string, timeouts).</summary>
public sealed record EquityPipelineProcessSettings(
    string PythonPath,
    string ScriptPath,
    string? DocumentsRoot,
    string ContentRoot,
    string? ConnectionString,
    IReadOnlyDictionary<string, string> ExtraEnv,
    TimeSpan BackfillTimeout,
    TimeSpan IncrementalTimeout,
    TimeSpan HeartbeatInterval)
{
    internal static string Resolve(string configured, string contentRoot)
    {
        if (Path.IsPathRooted(configured)) return Path.GetFullPath(configured);
        // A bare command name (e.g. "python") is looked up on PATH by the OS.
        if (!configured.Contains('/') && !configured.Contains('\\')) return configured;
        return Path.GetFullPath(Path.Combine(contentRoot, configured));
    }

    public static EquityPipelineProcessSettings From(EquityDocumentsOptions o, string contentRoot, string? connectionString) => new(
        Resolve(o.PythonPath, contentRoot),
        Resolve(o.PipelineScript, contentRoot),
        string.IsNullOrWhiteSpace(o.RootPath) ? null : Path.GetFullPath(Path.IsPathRooted(o.RootPath) ? o.RootPath : Path.Combine(contentRoot, o.RootPath)),
        Path.GetFullPath(contentRoot),
        connectionString,
        new Dictionary<string, string>(o.ExtraEnv ?? [], StringComparer.Ordinal),
        TimeSpan.FromMinutes(Math.Max(0.001, o.BackfillTimeoutMinutes)),
        TimeSpan.FromMinutes(Math.Max(0.001, o.IncrementalTimeoutMinutes)),
        TimeSpan.FromSeconds(Math.Max(0.05, o.SupervisorHeartbeatSeconds)));
}

/// <summary>
/// Scrubs subprocess output before it is stored/served: the DB password and connection string, any
/// password=/DB_PASS= style assignment, postgres URLs with credentials, values of secret-looking ExtraEnv
/// keys, and absolute paths (known roots → labels, any other drive path → <c>&lt;abs&gt;/filename</c>).
/// </summary>
public sealed class EquityPipelineLogScrubber
{
    private static readonly Regex Assignment = new(
        @"(?i)\b(password|passwd|pwd|db_pass|db_password|postgres_password|pgpassword|api[_-]?key|token|secret)(\s*[=:]\s*)(""[^""]*""|'[^']*'|[^\s;,'""]+)",
        RegexOptions.CultureInvariant);
    private static readonly Regex PgUrl = new(@"(?i)\bpostgres(?:ql)?://[^\s@/]+:[^\s@/]+@", RegexOptions.CultureInvariant);
    private static readonly Regex DrivePath = new(
        @"(?i)(?<![A-Za-z0-9])[A-Z]:[\\/](?:[^\s""'<>|*?\r\n:]*[\\/])?([^\s""'<>|*?\r\n:\\/]*)", RegexOptions.CultureInvariant);
    private static readonly Regex UncPath = new(@"\\\\[^\s""'<>|*?\r\n]+[\\/]([^\s""'<>|*?\r\n\\/]*)", RegexOptions.CultureInvariant);

    private readonly string[] _secrets;
    private readonly (string Path, string Label)[] _roots;

    public EquityPipelineLogScrubber(IEnumerable<string?> secrets, IEnumerable<(string? Path, string Label)> roots)
    {
        _secrets = secrets.Where(s => !string.IsNullOrEmpty(s) && s!.Length >= 4).Select(s => s!)
            .Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray();
        _roots = roots.Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .SelectMany(r => new[] { (r.Path!.TrimEnd('\\', '/'), r.Label), (r.Path!.TrimEnd('\\', '/').Replace('\\', '/'), r.Label) })
            .Where(r => r.Item1.Length >= 3)
            .Distinct().OrderByDescending(r => r.Item1.Length).ToArray();
    }

    public static EquityPipelineLogScrubber For(EquityPipelineProcessSettings s)
    {
        var secrets = new List<string?> { s.ConnectionString };
        if (!string.IsNullOrWhiteSpace(s.ConnectionString))
        {
            try { secrets.Add(new NpgsqlConnectionStringBuilder(s.ConnectionString).Password); }
            catch (ArgumentException) { }
        }
        foreach (var (key, value) in s.ExtraEnv)
            if (Regex.IsMatch(key, "(?i)pass|secret|token|key")) secrets.Add(value);
        var aiDir = Path.GetDirectoryName(s.ScriptPath);
        return new EquityPipelineLogScrubber(secrets,
        [
            (s.DocumentsRoot, "<documents-root>"),
            (aiDir, "<ai>"),
            (Path.IsPathRooted(s.PythonPath) ? Path.GetDirectoryName(s.PythonPath) : null, "<python>"),
            (s.ContentRoot, "<backend>"),
            (Path.GetDirectoryName(s.ContentRoot), "<workspace>"),
        ]);
    }

    public string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var secret in _secrets) text = text.Replace(secret, "***", StringComparison.Ordinal);
        text = PgUrl.Replace(text, "postgresql://***:***@");
        text = Assignment.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + "***");
        foreach (var (path, label) in _roots) text = text.Replace(path, label, StringComparison.OrdinalIgnoreCase);
        text = DrivePath.Replace(text, m => "<abs>/" + m.Groups[1].Value);
        text = UncPath.Replace(text, m => "<abs>/" + m.Groups[1].Value);
        return text;
    }

    /// <summary>Scrubs every string value of a JSON document (keeps structure valid).</summary>
    public string? ScrubJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        if (node is JsonValue root && root.TryGetValue<string>(out var rs)) return JsonValue.Create(Scrub(rs))!.ToJsonString();
        Walk(node);
        return node?.ToJsonString();

        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var key in o.Select(kv => kv.Key).ToArray())
                    {
                        if (o[key] is JsonValue v && v.TryGetValue<string>(out var s)) o[key] = JsonValue.Create(Scrub(s));
                        else Walk(o[key]);
                    }
                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++)
                    {
                        if (a[i] is JsonValue v && v.TryGetValue<string>(out var s)) a[i] = JsonValue.Create(Scrub(s));
                        else Walk(a[i]);
                    }
                    break;
            }
        }
    }
}

/// <summary>Bounded tail of interleaved stdout/stderr lines (thread-safe).</summary>
internal sealed class ProcessOutputTail(int capacity)
{
    private readonly StringBuilder _buffer = new();
    private readonly object _gate = new();

    public void Append(string line)
    {
        lock (_gate)
        {
            _buffer.Append(line).Append('\n');
            if (_buffer.Length > capacity) _buffer.Remove(0, _buffer.Length - capacity);
        }
    }

    public override string ToString()
    {
        lock (_gate) return _buffer.ToString();
    }
}

/// <summary>
/// Production runner: one background task per run that launches
/// <c>python -I -X utf8 equity_pipeline.py run --symbol S --mode M --run-id ID [--budget N] [--max-docs N] [--dry-run]</c>.
/// DB credentials travel only in the child environment (DB_HOST/DB_PORT/DB_USER/DB_NAME/DB_PASS), never on
/// the command line or in logs. While the process is alive the supervisor refreshes HeartbeatAtUtc (process
/// liveness; the Python side may refresh it too). Timeout / cancel / host shutdown kill the whole process tree.
/// </summary>
public sealed class EquityPipelineProcessRunner : IEquityPipelineRunner, IHostedService, IAsyncDisposable
{
    public const int LogTailChars = 8000;
    public const int ErrorChars = 2000;

    private static readonly string[] ReservedEnv =
    [
        "DB_HOST", "DB_PORT", "DB_USER", "DB_NAME", "DB_PASS", "DB_PASSWORD", "POSTGRES_PASSWORD",
        "PGPASSWORD", "PGUSER", "PGHOST", "PGPORT", "PGDATABASE", "PGPASSFILE", "PGSERVICE", "BTC_TEST_POSTGRES",
        "EQUITY_DOCUMENTS_ROOT",
    ];

    private sealed class ActiveRun(string symbol)
    {
        public string Symbol { get; } = symbol;
        public CancellationTokenSource Cts { get; } = new();
        public volatile bool Shutdown;
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private readonly IServiceScopeFactory _scopes;
    private readonly EquityPipelineProcessSettings _settings;
    private readonly EquityPipelineLogScrubber _scrubber;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EquityPipelineProcessRunner> _logger;
    private readonly ConcurrentDictionary<Guid, ActiveRun> _active = new();
    private int _stopped;

    public EquityPipelineProcessRunner(
        IServiceScopeFactory scopes,
        IOptions<EquityDocumentsOptions> options,
        IHostEnvironment environment,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<EquityPipelineProcessRunner> logger)
        : this(scopes, EquityPipelineProcessSettings.From(options.Value, environment.ContentRootPath,
            configuration.GetConnectionString("DefaultConnection")), timeProvider, logger)
    {
    }

    internal EquityPipelineProcessRunner(
        IServiceScopeFactory scopes, EquityPipelineProcessSettings settings, TimeProvider? timeProvider = null,
        ILogger<EquityPipelineProcessRunner>? logger = null)
    {
        _scopes = scopes;
        _settings = settings;
        _scrubber = EquityPipelineLogScrubber.For(settings);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EquityPipelineProcessRunner>.Instance;
    }

    public string Scrub(string text) => _scrubber.Scrub(text);

    public string? ScrubJson(string? json) => _scrubber.ScrubJson(json);

    public string? Unavailable()
    {
        if (Volatile.Read(ref _stopped) != 0) return "host-stopping";
        if (Path.IsPathRooted(_settings.PythonPath) && !File.Exists(_settings.PythonPath)) return "python-not-found";
        if (!File.Exists(_settings.ScriptPath)) return "pipeline-script-not-found";
        return null;
    }

    public bool IsActive(Guid runId) => _active.ContainsKey(runId);

    public bool IsSymbolActive(string symbol) => _active.Values.Any(a => a.Symbol == symbol);

    public bool TryCancel(Guid runId)
    {
        if (!_active.TryGetValue(runId, out var run)) return false;
        try { run.Cts.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }

    public Task Launch(EquityPipelineLaunch launch)
    {
        var run = new ActiveRun(launch.Symbol);
        if (!_active.TryAdd(launch.RunId, run)) throw new InvalidOperationException($"run {launch.RunId} is already supervised");
        run.Task = Task.Run(async () =>
        {
            try { await ExecuteAsync(launch, run); }
            catch (Exception ex) { _logger.LogError(ex, "Equity pipeline supervisor crashed for run {RunId}", launch.RunId); }
            finally
            {
                _active.TryRemove(launch.RunId, out _);
                run.Cts.Dispose();
            }
        });
        return run.Task;
    }

    /// <summary>Command line + environment of a run (no secrets in ArgumentList).</summary>
    internal ProcessStartInfo BuildStartInfo(EquityPipelineLaunch l)
    {
        var psi = new ProcessStartInfo(_settings.PythonPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(_settings.ScriptPath) ?? _settings.ContentRoot,
        };
        foreach (var a in new[] { "-I", "-X", "utf8", _settings.ScriptPath, "run", "--symbol", l.Symbol, "--mode", l.Mode, "--run-id", l.RunId.ToString("D") })
            psi.ArgumentList.Add(a);
        if (l.Budget is { } b) { psi.ArgumentList.Add("--budget"); psi.ArgumentList.Add(b.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        if (l.MaxDocs is { } m) { psi.ArgumentList.Add("--max-docs"); psi.ArgumentList.Add(m.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        if (l.DryRun) psi.ArgumentList.Add("--dry-run");
        psi.ArgumentList.Add("--extract-scope");
        psi.ArgumentList.Add(l.ExtractScope);

        foreach (var key in ReservedEnv) psi.Environment.Remove(key);
        foreach (var (key, value) in _settings.ExtraEnv)
            if (!ReservedEnv.Contains(key, StringComparer.OrdinalIgnoreCase)) psi.Environment[key] = value;
        if (!string.IsNullOrWhiteSpace(_settings.ConnectionString))
        {
            var cs = new NpgsqlConnectionStringBuilder(_settings.ConnectionString);
            psi.Environment["DB_HOST"] = cs.Host ?? "localhost";
            psi.Environment["DB_PORT"] = cs.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            psi.Environment["DB_USER"] = cs.Username ?? "";
            psi.Environment["DB_NAME"] = cs.Database ?? "";
            psi.Environment["DB_PASS"] = cs.Password ?? "";
        }
        if (_settings.DocumentsRoot is not null) psi.Environment["EQUITY_DOCUMENTS_ROOT"] = _settings.DocumentsRoot;
        psi.Environment["EQUITY_PIPELINE_RUN_ID"] = l.RunId.ToString("D");
        return psi;
    }

    private async Task ExecuteAsync(EquityPipelineLaunch l, ActiveRun run)
    {
        var tail = new ProcessOutputTail(LogTailChars * 2);
        int? exitCode = null;
        var cause = EquityPipelineTermination.None;
        string? startError = null;
        var timeout = l.Mode == EquityPipelineModes.Backfill ? _settings.BackfillTimeout : _settings.IncrementalTimeout;
        using var heartbeatCts = new CancellationTokenSource();
        Task heartbeat = Task.CompletedTask;
        try
        {
            using var process = new Process { StartInfo = BuildStartInfo(l), EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) tail.Append(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) tail.Append("[stderr] " + e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try { process.StandardInput.Close(); } catch (IOException) { }
            _logger.LogInformation("Equity pipeline run {RunId} {Symbol} {Mode} started (pid {Pid})", l.RunId, l.Symbol, l.Mode, process.Id);
            heartbeat = HeartbeatLoopAsync(l.RunId, heartbeatCts.Token);

            using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, run.Cts.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                var exitedFirst = process.HasExited;
                if (!exitedFirst)
                    cause = run.Cts.IsCancellationRequested
                        ? run.Shutdown ? EquityPipelineTermination.Shutdown : EquityPipelineTermination.Cancelled
                        : EquityPipelineTermination.Timeout;
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    _logger.LogWarning(ex, "Kill of pipeline run {RunId} reported an error", l.RunId);
                }
                using var killWait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(killWait.Token); }
                catch (OperationCanceledException) { }
            }
            if (process.HasExited)
            {
                process.WaitForExit(5000); // flush redirected output
                exitCode = process.ExitCode;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            startError = $"process-start-failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }
        await FinalizeAsync(l, exitCode, cause, startError, timeout, tail.ToString());
    }

    private async Task HeartbeatLoopAsync(Guid runId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_settings.HeartbeatInterval, _timeProvider, ct);
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var row = await db.EquityPipelineRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
                if (row is null || row.Status != EquityPipelineStatuses.Running) continue;
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                if (now > row.HeartbeatAtUtc) row.HeartbeatAtUtc = now;
                await db.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat update failed for pipeline run {RunId}", runId);
            }
        }
    }

    private static int Rank(string status) => status switch
    {
        EquityPipelineStatuses.Succeeded => 0,
        EquityPipelineStatuses.Partial => 1,
        EquityPipelineStatuses.Cancelled => 2,
        _ => 3,
    };

    /// <summary>
    /// Final status: termination cause first (timeout ⇒ failed, cancel/shutdown ⇒ cancelled), else the exit
    /// code (0 succeeded, 2 partial, 75 busy ⇒ failed, other ⇒ failed), then the more pessimistic of that and a
    /// terminal status the Python side wrote. "succeeded" is only possible with exit code 0.
    /// </summary>
    internal static (string Status, string? Error) FinalStatus(
        int? exitCode, EquityPipelineTermination cause, string? startError, TimeSpan timeout, string? pythonStatus, string? pythonError)
    {
        string status;
        string? error;
        if (startError is not null) (status, error) = (EquityPipelineStatuses.Failed, startError);
        else if (cause == EquityPipelineTermination.Timeout)
            (status, error) = (EquityPipelineStatuses.Failed, $"timeout after {timeout.TotalMinutes:0.##} min (process tree killed)");
        else if (cause == EquityPipelineTermination.Cancelled)
            (status, error) = (EquityPipelineStatuses.Cancelled, "cancelled by admin (process tree killed)");
        else if (cause == EquityPipelineTermination.Shutdown)
            (status, error) = (EquityPipelineStatuses.Cancelled, "host-shutdown (process tree killed)");
        else (status, error) = exitCode switch
        {
            null => (EquityPipelineStatuses.Failed, "exit-code-unknown"),
            EquityPipelineExitCodes.Succeeded => (EquityPipelineStatuses.Succeeded, null),
            EquityPipelineExitCodes.Partial => (EquityPipelineStatuses.Partial, (string?)null),
            EquityPipelineExitCodes.Busy => (EquityPipelineStatuses.Failed, "pipeline-busy: advisory lock equity-docs:<symbol> held by another process (exit 75)"),
            _ => (EquityPipelineStatuses.Failed, $"exit code {exitCode}"),
        };
        var py = pythonStatus is EquityPipelineStatuses.Succeeded or EquityPipelineStatuses.Partial
            or EquityPipelineStatuses.Failed or EquityPipelineStatuses.Cancelled ? pythonStatus : null;
        if (py is not null && Rank(py) > Rank(status)) status = py;
        if (!string.IsNullOrWhiteSpace(pythonError) && pythonError != error)
            error = error is null ? pythonError : $"{error}; python: {pythonError}";
        return (status, error);
    }

    private async Task FinalizeAsync(EquityPipelineLaunch l, int? exitCode, EquityPipelineTermination cause, string? startError,
        TimeSpan timeout, string rawTail)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.EquityPipelineRuns.FirstOrDefaultAsync(r => r.Id == l.RunId);
            if (row is null)
            {
                _logger.LogWarning("Pipeline run {RunId} row disappeared before finalization", l.RunId);
                return;
            }
            var (status, error) = FinalStatus(exitCode, cause, startError, timeout,
                row.Status == EquityPipelineStatuses.Running ? null : row.Status, row.Error);
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (now < row.StartedAtUtc) now = row.StartedAtUtc;
            row.Status = status;
            row.FinishedAtUtc ??= now;
            if (row.FinishedAtUtc < row.StartedAtUtc) row.FinishedAtUtc = now;
            if (row.HeartbeatAtUtc < now) row.HeartbeatAtUtc = now;
            row.Error = Truncate(error is null ? null : _scrubber.Scrub(error), ErrorChars);
            var scrubbed = _scrubber.Scrub((exitCode is { } c ? $"[supervisor] exit code {c}\n" : "") + rawTail);
            row.LogTail = scrubbed.Length > LogTailChars ? scrubbed[^LogTailChars..] : scrubbed;
            row.CheckpointJson = WithLaunch(row.CheckpointJson, l);
            await db.SaveChangesAsync(CancellationToken.None);
            _logger.LogInformation("Equity pipeline run {RunId} {Symbol} finished: {Status} (exit {Exit})", l.RunId, l.Symbol, status, exitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not finalize pipeline run {RunId}", l.RunId);
        }
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    /// <summary>Keeps CheckpointJson.launch present (the Python side owns the rest of the checkpoint).</summary>
    internal static string? WithLaunch(string? checkpointJson, EquityPipelineLaunch l)
    {
        JsonNode? node = null;
        if (!string.IsNullOrWhiteSpace(checkpointJson))
        {
            try { node = JsonNode.Parse(checkpointJson); }
            catch (JsonException) { return checkpointJson; }
        }
        if (node is null) return new JsonObject { ["launch"] = l.LaunchJson() }.ToJsonString();
        if (node is not JsonObject obj || obj.ContainsKey("launch")) return checkpointJson;
        obj["launch"] = l.LaunchJson();
        return obj.ToJsonString();
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Host shutdown: kill every supervised process tree and record the runs as cancelled (host-shutdown).</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _stopped, 1);
        var runs = _active.Values.ToArray();
        foreach (var run in runs)
        {
            run.Shutdown = true;
            try { run.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        try { await Task.WhenAll(runs.Select(r => r.Task)).WaitAsync(TimeSpan.FromSeconds(45), cancellationToken); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("Pipeline runs did not finalize before shutdown");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_active.IsEmpty) await StopAsync(CancellationToken.None);
        Interlocked.Exchange(ref _stopped, 1);
    }
}

/// <summary>Run creation, lock, cancel and listing (scoped; the subprocess itself lives in <see cref="IEquityPipelineRunner"/>).</summary>
public sealed class EquityDocumentPipelineService
{
    public const string RunningIndexName = "UX_EquityPipelineRuns_OneRunningPerSymbol";
    public const string StaleHeartbeatError = "stale-heartbeat";
    private const string InsertSavepoint = "eqx_pipeline_insert";

    public enum Outcome { Started, Busy, Invalid, Unavailable, CancelRequested, NotFound, NotRunning, NotOwned }

    public sealed record Result(Outcome Outcome, string? Code, string? Message, EquityPipelineRunDto? Run, Task? Completion = null);

    private readonly AppDbContext _db;
    private readonly IEquityPipelineRunner _runner;
    private readonly EquityDocumentsOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly InstrumentCatalog _catalog;

    public EquityDocumentPipelineService(AppDbContext db, IEquityPipelineRunner runner, IOptions<EquityDocumentsOptions> options, TimeProvider timeProvider)
        : this(db, runner, options.Value, timeProvider, null)
    {
    }

    internal EquityDocumentPipelineService(AppDbContext db, IEquityPipelineRunner runner, EquityDocumentsOptions options,
        TimeProvider? timeProvider, InstrumentCatalog? catalog)
    {
        _db = db;
        _runner = runner;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _catalog = catalog ?? InstrumentCatalog.Default;
    }

    private string RequireDecision(string symbol)
    {
        var instrument = _catalog.Get(symbol)
            ?? throw new ArgumentException($"Unknown symbol '{InstrumentCatalog.Canonicalize(symbol)}'.", nameof(symbol));
        if (!EquityScope.Has(instrument, EquityCapability.Decision))
            throw new ArgumentException($"Symbol '{instrument.Symbol}' does not support capability 'decision'.", nameof(symbol));
        return instrument.Symbol;
    }

    private static Result Invalid(string message) => new(Outcome.Invalid, EquityDocumentPipelineContracts.InvalidPipelineRequest, message, null);

    public async Task<Result> StartAsync(string symbol, string? mode, string trigger, int? budget = null, int? maxDocs = null,
        bool dryRun = false, CancellationToken ct = default, string? extractScope = null)
    {
        var sym = RequireDecision(symbol);
        if (mode is not (EquityPipelineModes.Backfill or EquityPipelineModes.Incremental))
            return Invalid("mode must be backfill or incremental.");
        if (trigger is not ("worker" or "admin" or "cli")) return Invalid("trigger must be worker, admin or cli.");
        if (budget is < 1 or > 100_000) return Invalid("budget must be within 1..100000.");
        if (maxDocs is < 1 or > 100_000) return Invalid("maxDocs must be within 1..100000.");
        var scope = string.IsNullOrWhiteSpace(extractScope) ? EquityPipelineExtractScopes.Consolidated : extractScope.Trim();
        if (!EquityPipelineExtractScopes.IsValid(scope)) return Invalid("extractScope must be consolidated or all.");
        var launch = new EquityPipelineLaunch(Guid.NewGuid(), sym, mode, budget, maxDocs, dryRun, scope);
        if (_runner.Unavailable() is { } why)
            return new(Outcome.Unavailable, EquityDocumentPipelineContracts.PipelineUnavailable,
                $"The document pipeline cannot be launched: {why}.", null);

        await MarkStaleAsync(sym, ct);
        if (_runner.IsSymbolActive(sym) || (!_db.Database.IsRelational() && await RunningAsync(sym, ct) is not null))
            return await BusyAsync(sym, ct);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var row = new EquityPipelineRun
        {
            Id = launch.RunId, Symbol = sym, Mode = mode, Trigger = trigger, Status = EquityPipelineStatuses.Running,
            StartedAtUtc = now, HeartbeatAtUtc = now,
            CheckpointJson = new JsonObject { ["launch"] = launch.LaunchJson() }.ToJsonString(),
        };
        var tx = _db.Database.CurrentTransaction;
        if (tx is not null) await tx.CreateSavepointAsync(InsertSavepoint, ct);
        _db.EquityPipelineRuns.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
            if (tx is not null) await tx.ReleaseSavepointAsync(InsertSavepoint, ct);
        }
        catch (DbUpdateException ex) when (IsOneRunningViolation(ex))
        {
            if (tx is not null) await tx.RollbackToSavepointAsync(InsertSavepoint, CancellationToken.None);
            _db.Entry(row).State = EntityState.Detached;
            return await BusyAsync(sym, ct);
        }
        _db.Entry(row).State = EntityState.Detached;
        var completion = _runner.Launch(launch);
        return new(Outcome.Started, null, null, ToDto(row), completion);
    }

    /// <summary>True when the insert hit the partial unique index (one running run per symbol).</summary>
    public static bool IsOneRunningViolation(Exception ex) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException) is { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && pg.ConstraintName == RunningIndexName;

    private async Task<EquityPipelineRun?> RunningAsync(string sym, CancellationToken ct) =>
        await _db.EquityPipelineRuns.AsNoTracking().Where(r => r.Symbol == sym && r.Status == EquityPipelineStatuses.Running)
            .OrderByDescending(r => r.StartedAtUtc).FirstOrDefaultAsync(ct);

    private async Task<Result> BusyAsync(string sym, CancellationToken ct)
    {
        var running = await RunningAsync(sym, ct);
        return new(Outcome.Busy, EquityDocumentPipelineContracts.PipelineBusy,
            running is null
                ? $"A {sym} document pipeline run is already in progress."
                : $"{sym} document pipeline run {running.Id} ({running.Mode}, {running.Trigger}) is running since {running.StartedAtUtc:O}.",
            running is null ? null : ToDto(running));
    }

    /// <summary>
    /// Marks running rows whose heartbeat is older than StaleHeartbeatMinutes (and that this process does not
    /// supervise) as failed / stale-heartbeat. Returns how many rows were recovered.
    /// </summary>
    public async Task<int> MarkStaleAsync(string symbol, CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var limit = now - TimeSpan.FromMinutes(Math.Max(1, _options.StaleHeartbeatMinutes));
        var stale = await _db.EquityPipelineRuns
            .Where(r => r.Symbol == symbol && r.Status == EquityPipelineStatuses.Running && r.HeartbeatAtUtc < limit)
            .ToListAsync(ct);
        var n = 0;
        foreach (var r in stale.Where(r => !_runner.IsActive(r.Id)))
        {
            r.Status = EquityPipelineStatuses.Failed;
            r.FinishedAtUtc = now < r.StartedAtUtc ? r.StartedAtUtc : now;
            r.Error = $"{StaleHeartbeatError}: last heartbeat {r.HeartbeatAtUtc:O} older than {_options.StaleHeartbeatMinutes:0.##} min";
            n++;
        }
        if (n > 0) await _db.SaveChangesAsync(ct);
        foreach (var r in stale) _db.Entry(r).State = EntityState.Detached;
        return n;
    }

    public async Task<Result> CancelAsync(string symbol, Guid runId, CancellationToken ct = default)
    {
        var sym = RequireDecision(symbol);
        var row = await _db.EquityPipelineRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId && r.Symbol == sym, ct);
        if (row is null)
            return new(Outcome.NotFound, EquityDocumentPipelineContracts.RunNotFound, $"No {sym} pipeline run {runId}.", null);
        if (row.Status != EquityPipelineStatuses.Running)
            return new(Outcome.NotRunning, EquityDocumentPipelineContracts.RunNotRunning, $"Run {runId} is already {row.Status}.", ToDto(row));
        if (_runner.TryCancel(runId))
            return new(Outcome.CancelRequested, null, null, ToDto(row));
        if (await MarkStaleAsync(sym, ct) > 0)
        {
            var after = await _db.EquityPipelineRuns.AsNoTracking().FirstAsync(r => r.Id == runId, ct);
            if (after.Status != EquityPipelineStatuses.Running)
                return new(Outcome.NotRunning, EquityDocumentPipelineContracts.RunNotRunning,
                    $"Run {runId} had a stale heartbeat and was marked {after.Status}.", ToDto(after));
        }
        return new(Outcome.NotOwned, EquityDocumentPipelineContracts.RunNotOwned,
            $"Run {runId} is not supervised by this backend process (CLI or another host); it cannot be killed from here.", ToDto(row));
    }

    public async Task<EquityPipelineRunListResponse> ListAsync(string symbol, int take, CancellationToken ct = default)
    {
        var sym = RequireDecision(symbol);
        take = Math.Clamp(take, 1, 200);
        var rows = await _db.EquityPipelineRuns.AsNoTracking().Where(r => r.Symbol == sym)
            .OrderByDescending(r => r.StartedAtUtc).ThenByDescending(r => r.Id).Take(take).ToListAsync(ct);
        return new EquityPipelineRunListResponse(EquityDocumentPipelineContracts.PipelineRunsVersion, sym, rows.Select(ToDto).ToArray());
    }

    public async Task<EquityPipelineRunDto?> GetAsync(Guid runId, CancellationToken ct = default)
    {
        var row = await _db.EquityPipelineRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        return row is null ? null : ToDto(row);
    }

    /// <summary>CheckpointJson.launch.extractScope (null for runs created without it, e.g. by the CLI).</summary>
    internal static string? LaunchScope(string? checkpointJson)
    {
        if (string.IsNullOrWhiteSpace(checkpointJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(checkpointJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("launch", out var l)
                && l.ValueKind == JsonValueKind.Object && l.TryGetProperty("extractScope", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    internal EquityPipelineRunDto ToDto(EquityPipelineRun r)
    {
        return new EquityPipelineRunDto(r.Id, r.Symbol, r.Mode, r.Trigger, r.Status,
            EquityFinancialFactsService.Utc(r.StartedAtUtc), EquityFinancialFactsService.Utc(r.HeartbeatAtUtc),
            r.FinishedAtUtc is { } f ? EquityFinancialFactsService.Utc(f) : null,
            EquityFinancialFactsService.Json(_runner.ScrubJson(r.CountsJson)), EquityFinancialFactsService.Json(_runner.ScrubJson(r.CheckpointJson)),
            r.Error is null ? null : _runner.Scrub(r.Error), r.LogTail is null ? null : _runner.Scrub(r.LogTail),
            _runner.IsActive(r.Id), LaunchScope(r.CheckpointJson));
    }
}

/// <summary>
/// Daily incremental document poll (spec v3 §4). Exits immediately unless EquityDocuments:PipelineEnabled=true
/// (default false, not enabled in any appsettings; registered only with BackgroundWorkers). Each day at
/// PipelineRunAtLocal (Asia/Ho_Chi_Minh) — and once on start-up when today's slot already passed without a
/// worker run — it walks PipelineSymbols SEQUENTIALLY: a symbol with a worker run since the slot is skipped,
/// a busy symbol is skipped, one symbol failing never blocks the next. Heartbeat
/// (WorkerHeartbeatStore "EquityDocumentPipelineWorker") is PROCESS liveness only: failed/partial runs are a
/// Partial cycle with per-symbol detail, never a Failed heartbeat (AGENTS pitfall #40). It is deliberately not
/// in HealthController.ExpectedWorkers (daily cadence must not trip the stale-restart watchdog).
/// </summary>
public sealed class EquityDocumentPipelineWorker : BackgroundService
{
    public const string WorkerName = "EquityDocumentPipelineWorker";

    private readonly IServiceScopeFactory _scopes;
    private readonly EquityDocumentsOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly InstrumentCatalog _catalog;
    private readonly ILogger<EquityDocumentPipelineWorker> _logger;

    public EquityDocumentPipelineWorker(IServiceScopeFactory scopes, IOptions<EquityDocumentsOptions> options,
        TimeProvider? timeProvider = null, InstrumentCatalog? catalog = null, ILogger<EquityDocumentPipelineWorker>? logger = null)
    {
        _scopes = scopes;
        _options = options.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _catalog = catalog ?? InstrumentCatalog.Default;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EquityDocumentPipelineWorker>.Instance;
    }

    internal TimeOnly RunAt() =>
        TimeOnly.TryParseExact(_options.PipelineRunAtLocal, "HH:mm", out var t) ? t : new TimeOnly(18, 30);

    /// <summary>Most recent scheduled slot (UTC) at or before <paramref name="nowUtc"/>.</summary>
    internal DateTime LatestSlotUtc(DateTimeOffset nowUtc)
    {
        var nowMs = nowUtc.ToUnixTimeMilliseconds();
        var today = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(nowMs).DateTime);
        var slot = VietnamMarketCalendar.ToUtcMs(today, RunAt());
        if (slot > nowMs) slot = VietnamMarketCalendar.ToUtcMs(today.AddDays(-1), RunAt());
        return DateTimeOffset.FromUnixTimeMilliseconds(slot).UtcDateTime;
    }

    internal IReadOnlyList<string> Symbols() =>
        (_options.PipelineSymbols ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(InstrumentCatalog.Canonicalize)
            .Distinct(StringComparer.Ordinal)
            .Where(s => _catalog.Get(s) is { } i && EquityScope.Has(i, EquityCapability.Decision)).ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.PipelineEnabled)
        {
            _logger.LogInformation("EquityDocumentPipelineWorker disabled (EquityDocuments:PipelineEnabled=false)");
            return;
        }
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var slot = LatestSlotUtc(_timeProvider.GetUtcNow());
                await RunCycleWithHeartbeatAsync(slot, stoppingToken);
                var wait = slot.AddDays(1) - _timeProvider.GetUtcNow().UtcDateTime;
                await Task.Delay(wait < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait, _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RunCycleWithHeartbeatAsync(DateTime slotUtc, CancellationToken ct)
    {
        var started = _timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            using (var scope = _scopes.CreateScope())
                await WorkerHeartbeatStore.MarkStartedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), WorkerName, started, ct);
            var report = await RunOnceAsync(slotUtc, ct);
            using (var scope = _scopes.CreateScope())
                await WorkerHeartbeatStore.MarkCompletedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), WorkerName, started,
                    _timeProvider.GetUtcNow().UtcDateTime, report, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EquityDocumentPipelineWorker cycle crashed");
            try
            {
                using var scope = _scopes.CreateScope();
                await WorkerHeartbeatStore.MarkFailedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), WorkerName, started,
                    _timeProvider.GetUtcNow().UtcDateTime, ex, ct);
            }
            catch (Exception inner) when (inner is not OperationCanceledException)
            {
                _logger.LogWarning(inner, "EquityDocumentPipelineWorker could not record its failed heartbeat");
            }
        }
    }

    /// <summary>One slot over every symbol, sequential. Never throws for a symbol's failure.</summary>
    internal async Task<WorkerCycleReport> RunOnceAsync(DateTime slotUtc, CancellationToken ct)
    {
        int attempted = 0, succeeded = 0, failed = 0, skipped = 0;
        var notes = new List<string>();
        foreach (var sym in Symbols())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Task? completion;
                Guid runId;
                using (var scope = _scopes.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var done = await db.EquityPipelineRuns.AsNoTracking().AnyAsync(r => r.Symbol == sym && r.Trigger == "worker"
                        && r.StartedAtUtc >= slotUtc && r.Status != EquityPipelineStatuses.Running, ct);
                    if (done) { skipped++; continue; }
                    var service = scope.ServiceProvider.GetRequiredService<EquityDocumentPipelineService>();
                    var result = await service.StartAsync(sym, EquityPipelineModes.Incremental, "worker",
                        _options.WorkerBudget, _options.WorkerMaxDocs, false, ct, _options.WorkerExtractScope);
                    if (result.Outcome == EquityDocumentPipelineService.Outcome.Busy)
                    {
                        skipped++;
                        notes.Add($"{sym}=busy");
                        continue;
                    }
                    if (result.Outcome != EquityDocumentPipelineService.Outcome.Started)
                    {
                        attempted++;
                        failed++;
                        notes.Add($"{sym}={result.Code}");
                        continue;
                    }
                    completion = result.Completion;
                    runId = result.Run!.RunId;
                }
                attempted++;
                if (completion is not null) await completion.WaitAsync(ct);
                using (var scope = _scopes.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var status = await db.EquityPipelineRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).FirstOrDefaultAsync(ct);
                    if (status == EquityPipelineStatuses.Succeeded) succeeded++;
                    else
                    {
                        failed++;
                        notes.Add($"{sym}={status ?? "unknown"}");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Document pipeline poll failed for {Symbol}", sym);
                attempted++;
                failed++;
                notes.Add($"{sym}=error:{ex.GetType().Name}");
            }
        }
        var outcome = attempted == 0 ? WorkerCycleOutcome.Idle : failed > 0 ? WorkerCycleOutcome.Partial : WorkerCycleOutcome.Succeeded;
        return new WorkerCycleReport(outcome, attempted, succeeded, failed, skipped, 0,
            notes.Count == 0 ? $"slot {slotUtc:O}" : $"slot {slotUtc:O}: {string.Join(", ", notes)}");
    }
}
