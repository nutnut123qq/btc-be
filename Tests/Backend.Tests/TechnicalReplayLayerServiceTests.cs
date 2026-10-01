using Backend.Data;
using Backend.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Backend.Tests;

public sealed class TechnicalReplayLayerServiceTests
{
    private readonly TechnicalReplayLayerService _service = new(
        new BuiltInTechnicalModuleContractProvider());

    [Fact]
    public void Indicators_require_two_complete_sixty_bar_windows_for_cross_events()
    {
        var rows = Candles(61);

        var unavailable = _service.Build(rows[59].CloseTimeMs, rows.Take(60).ToArray(), rows[55].OpenTimeMs);
        var available = _service.Build(rows[60].CloseTimeMs, rows, rows[56].OpenTimeMs);

        Assert.Equal("unavailable", unavailable.Indicators.Availability);
        Assert.Equal(61, unavailable.Indicators.Lineage.RequiredWarmupBars);
        Assert.Equal("available", available.Indicators.Availability);
        Assert.Equal(61, available.Indicators.Lineage.SourceCandleCount);
        Assert.Equal(rows[0].OpenTimeMs, available.Indicators.Lineage.SourceStartTimeMs);
        Assert.Equal(rows[^1].CloseTimeMs, available.Indicators.Lineage.SourceEndTimeMs);
    }

    [Fact]
    public void Volume_profile_and_volume_anomaly_fail_closed_without_positive_volume()
    {
        var rows = Candles(101, volume: 0m);

        var result = _service.Build(rows[^1].CloseTimeMs, rows, rows[^5].OpenTimeMs);

        Assert.Equal("unavailable", result.VolumeProfile.Availability);
        Assert.Contains("positive volume", result.VolumeProfile.UnavailableReason);
        Assert.Equal("unavailable", result.VolumeAnomaly.Availability);
        Assert.Contains("mean volume", result.VolumeAnomaly.UnavailableReason);
    }

    [Fact]
    public void Coverage_reports_each_defining_source_window_not_global_context()
    {
        var rows = Candles(120);
        var layers = _service.Build(rows[^1].CloseTimeMs, rows, rows[^5].OpenTimeMs);

        var coverage = _service.BuildCoverage(layers, hasGapBoundary: false)
            .ToDictionary(x => x.LayerKey);

        Assert.Equal(61, coverage["technicalIndicators"].SourceBars);
        Assert.Equal(21, coverage["volumeAnomaly"].SourceBars);
        Assert.Equal(22, coverage["marketRegime"].SourceBars);
        Assert.Equal(101, coverage["volumeProfile"].SourceBars);
    }

    [Fact]
    public void Volume_profile_window_end_is_the_finalized_close_time()
    {
        var rows = Candles(101);

        var result = _service.Build(rows[^1].CloseTimeMs, rows, rows[^5].OpenTimeMs);

        Assert.Equal(rows[^1].CloseTimeMs, result.VolumeProfile.Payload!.WindowEndMs);
    }

    [Fact]
    public void Regime_can_emit_first_transition_after_ten_prior_segment_ranges()
    {
        var rows = Candles(12);
        // Previous decision is sideways/normal; the final close creates four rising changes.
        for (var i = 0; i <= 7; i++) rows[i].Close = 100m;
        rows[8].Close = 101m;
        rows[9].Close = 102m;
        rows[10].Close = 103m;
        rows[11].Close = 104m;
        foreach (var row in rows)
        {
            row.High = row.Close + 1m;
            row.Low = row.Close - 1m;
            row.Open = row.Close;
        }

        var result = _service.Build(rows[^1].CloseTimeMs, rows, rows[^5].OpenTimeMs);

        Assert.Equal("available", result.MarketRegime.Availability);
        Assert.Equal("REGIME_BULL_NORMAL", result.MarketRegime.Payload!.EventType);
    }

    [Fact]
    public void Canonical_golden_fixture_matches_backend_ordered_semantic_event_ledger()
    {
        var backendRoot = FindBackendRoot();
        var contractBytes = File.ReadAllBytes(Path.Combine(backendRoot, "contracts", "technical-module-contract.json"));
        var provider = new TechnicalModuleContractProvider(Encoding.UTF8.GetString(contractBytes));
        Assert.Equal("921f98700bc2afabdc4cf825563cd3ac23d9cefbf4f7c7c7bc59baf422a46057", provider.Sha256);

        var aiCopy = Path.GetFullPath(Path.Combine(backendRoot, "..", "ai", "contracts", "technical-module-contract.json"));
        if (File.Exists(aiCopy)) Assert.Equal(File.ReadAllBytes(aiCopy), contractBytes);

        const long interval = 14_400_000;
        var rows = Enumerable.Range(0, 240).Select(i =>
        {
            var basePrice = 100_000 + 13 * i + (i % 12 - 6) * 200;
            var close = basePrice + (i % 5 - 2) * 40;
            return new Kline
            {
                Symbol = "BTCUSDT", Timeframe = "4h",
                OpenTimeMs = i * interval, CloseTimeMs = (i + 1) * interval - 1,
                Open = basePrice, Close = close,
                High = Math.Max(basePrice, close) + 100 + i % 3 * 10,
                Low = Math.Min(basePrice, close) - 110 - i % 4 * 10,
                Volume = (1_000 + i % 7 * 100) * (i % 29 == 0 ? 4 : 1)
            };
        }).ToList();
        var service = new TechnicalReplayLayerService(provider);
        var events = new List<(string Module, string Type, long Available)>();
        for (var end = 0; end < rows.Count; end++)
        {
            var prefix = rows.Take(end + 1).ToArray();
            var close = prefix[^1].CloseTimeMs;
            var layers = service.Build(close, prefix, prefix[^1].OpenTimeMs);
            if (end == rows.Count - 1)
            {
                Assert.Equal("available", layers.Fibonacci.Availability);
                Assert.Equal(close, layers.Fibonacci.Lineage.EffectiveAsOfTimeMs);
                Assert.True(layers.Fibonacci.Lineage.AvailableTimeMs <= close);
            }
            events.AddRange(layers.Indicators.Payload?.Events.Select(x => ("technicalIndicators", x.EventType, close)) ?? []);
            events.AddRange(layers.CandlePatterns.Payload?.Events.Where(x => x.AvailableTimeMs == close)
                .Select(x => ("candlePatterns", x.PatternType, close)) ?? []);
            events.AddRange(layers.VolumeAnomaly.Payload?.TriggeredEvents.Select(x => ("volumeAnomaly", x, close)) ?? []);
            if (layers.MarketRegime.Payload?.EventType is { } regime)
                events.Add(("marketRegime", regime, close));
            if (layers.Fibonacci.Payload?.AvailableTimeMs == close)
                events.Add(("fibonacci", layers.Fibonacci.Payload.EventType, close));
            events.AddRange(layers.VolumeProfile.Payload?.Events.Select(x => ("volumeProfile", x, close)) ?? []);
            events.AddRange(layers.Confluence.Payload?.TriggeredEvents.Select(x => ("confluence", x, close)) ?? []);
        }

        var ordered = events.OrderBy(x => x.Available).ThenBy(x => x.Module, StringComparer.Ordinal)
            .ThenBy(x => x.Type, StringComparer.Ordinal).ToArray();
        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(contractBytes));
        var expected = document.RootElement.GetProperty("goldenFixture").GetProperty("expected");
        Assert.Equal(expected.GetProperty("totalEvents").GetInt32(), ordered.Length);
        foreach (var property in expected.GetProperty("eventRowsByModule").EnumerateObject())
            Assert.Equal(property.Value.GetInt32(), ordered.Count(x => x.Module == property.Name));

        var semanticJson = "[" + string.Join(',', ordered.Select(x =>
            $"{{\"availableTimeMs\":{x.Available},\"eventType\":\"{x.Type}\",\"module\":\"{x.Module}\"}}")) + "]";
        var semanticHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(semanticJson))).ToLowerInvariant();
        Assert.Equal(expected.GetProperty("crossLanguageSemanticLedgerSha256").GetString(), semanticHash);
    }

    [Fact]
    public void Contract_provider_fails_closed_when_golden_fixture_is_missing()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new TechnicalModuleContractProvider("{\"contractVersion\":\"test/v1\",\"modules\":{}}"));

        Assert.Contains("goldenFixture.expected", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Layer_service_fails_closed_when_hardcoded_version_drifts_from_contract()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new TechnicalReplayLayerService(new MismatchedContractProvider()));

        Assert.Contains("technicalIndicators", error.Message, StringComparison.Ordinal);
        Assert.Contains("version mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindBackendRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Backend.csproj")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Backend.csproj root not found.");
    }

    private sealed class MismatchedContractProvider : ITechnicalModuleContractProvider
    {
        public string ContractVersion => "test/v1";
        public string Sha256 => new('1', 64);
        public string RawFileSha256 => new('2', 64);
        public string CanonicalJson => "{}";
        public string GetCalculationVersion(string moduleKey) => moduleKey == "technicalIndicators"
            ? "drifted-version"
            : new BuiltInTechnicalModuleContractProvider().GetCalculationVersion(moduleKey);
    }

    private static List<Kline> Candles(int count, decimal volume = 10m)
    {
        const long hour = 3_600_000;
        return Enumerable.Range(0, count).Select(i => new Kline
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            OpenTimeMs = 1_700_000_000_000L + i * hour,
            CloseTimeMs = 1_700_000_000_000L + (i + 1) * hour - 1,
            Open = 100m + i,
            High = 102m + i,
            Low = 99m + i,
            Close = 101m + i,
            Volume = volume
        }).ToList();
    }
}
