using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backend.Services;

/// <summary>Verification states of a canonical calendar day (EQX v2 A1).</summary>
public static class MarketCalendarVerification
{
    /// <summary>Backed by an official notice that was actually read (HOSE / SSC / Government + HOSE schedule).</summary>
    public const string Verified = "verified";

    /// <summary>Carried over from the old config list without an official document; still treated as closed.</summary>
    public const string UnverifiedLegacy = "unverified-legacy";
}

/// <summary>Kinds of a canonical non-trading day.</summary>
public static class MarketCalendarKinds
{
    public const string MarketHoliday = "market-holiday";
    public const string MarketClosure = "market-closure";
}

public sealed record MarketCalendarCoverage(
    [property: JsonPropertyName("fromDate")] string FromDate,
    [property: JsonPropertyName("toDate")] string ToDate);

public sealed record MarketCalendarFileDay(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("sourceUrl")] string? SourceUrl,
    [property: JsonPropertyName("sourceTitle")] string? SourceTitle,
    [property: JsonPropertyName("verification")] string Verification);

public sealed record MarketCalendarFileDocument(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("venue")] string Venue,
    [property: JsonPropertyName("calendarVersion")] string? CalendarVersion,
    [property: JsonPropertyName("coverage")] MarketCalendarCoverage Coverage,
    [property: JsonPropertyName("notes")] IReadOnlyList<string>? Notes,
    [property: JsonPropertyName("days")] IReadOnlyList<MarketCalendarFileDay> Days);

/// <summary>One parsed, validated non-trading day.</summary>
public sealed record MarketCalendarEntry(
    DateOnly Date,
    string Kind,
    string Reason,
    string? SourceUrl,
    string? SourceTitle,
    string Verification);

/// <summary>
/// Parsed canonical HOSE calendar (<c>Data/Calendars/vn-hose-non-trading-days.json</c>) plus the
/// config override dates that were unioned on top of it. This is the single source of truth for
/// the backend; the Python jobs read the same set from the synced <c>MarketCalendarDays</c> table.
/// </summary>
public sealed class MarketCalendarSource
{
    public const string SchemaVersion = "vn-hose-calendar-v1";
    public const string Venue = "HOSE";
    public const string DefaultRelativePath = "Data/Calendars/vn-hose-non-trading-days.json";

    public MarketCalendarSource(
        string calendarVersion,
        DateOnly coverageFrom,
        DateOnly coverageTo,
        IReadOnlyList<MarketCalendarEntry> days,
        IReadOnlyList<DateOnly> configOverrideDates,
        string? filePath)
    {
        CalendarVersion = calendarVersion;
        CoverageFrom = coverageFrom;
        CoverageTo = coverageTo;
        Days = days;
        ConfigOverrideDates = configOverrideDates;
        FilePath = filePath;
    }

    public string CalendarVersion { get; }
    public DateOnly CoverageFrom { get; }
    public DateOnly CoverageTo { get; }

    /// <summary>Days from the canonical file, ascending.</summary>
    public IReadOnlyList<MarketCalendarEntry> Days { get; }

    /// <summary>Config dates (MbbIngestion:NonTradingDates) that the file does NOT contain — reported, not hidden.</summary>
    public IReadOnlyList<DateOnly> ConfigOverrideDates { get; }
    public string? FilePath { get; }

    public int VerifiedCount => Days.Count(d => d.Verification == MarketCalendarVerification.Verified);
    public int UnverifiedLegacyCount => Days.Count(d => d.Verification == MarketCalendarVerification.UnverifiedLegacy);

    /// <summary>All non-trading dates: canonical file ∪ config overrides.</summary>
    public IEnumerable<DateOnly> AllNonTradingDates() => Days.Select(d => d.Date).Concat(ConfigOverrideDates);

    public MarketCalendarSource WithOverrides(IEnumerable<DateOnly> configured)
    {
        var fileDates = Days.Select(d => d.Date).ToHashSet();
        var extra = configured.Where(d => !fileDates.Contains(d)).Distinct().OrderBy(d => d).ToArray();
        return new MarketCalendarSource(CalendarVersion, CoverageFrom, CoverageTo, Days, extra, FilePath);
    }

    /// <summary>Resolve the canonical file next to the running assembly (copied on build).</summary>
    public static string ResolveDefaultPath(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(AppContext.BaseDirectory, configuredPath);
        return Path.Combine(AppContext.BaseDirectory, DefaultRelativePath);
    }

    private static readonly Lazy<MarketCalendarSource> DefaultSource =
        new(() => LoadFile(ResolveDefaultPath()), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The checked-in canonical calendar (cached). Throws when the file is missing or invalid (fail closed).</summary>
    public static MarketCalendarSource Default => DefaultSource.Value;

    public static MarketCalendarSource LoadFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Canonical market calendar file not found at '{path}'. It is copied from {DefaultRelativePath} on build.", path);
        var json = File.ReadAllText(path);
        return Parse(json, path);
    }

    /// <summary>Parse + validate the canonical document. Invalid content throws (never a silent empty calendar).</summary>
    public static MarketCalendarSource Parse(string json, string? filePath = null)
    {
        MarketCalendarFileDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<MarketCalendarFileDocument>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Market calendar file is not valid JSON: {ex.Message}", ex);
        }
        if (doc is null) throw new InvalidDataException("Market calendar file is empty.");
        if (doc.SchemaVersion != SchemaVersion)
            throw new InvalidDataException($"Market calendar schemaVersion '{doc.SchemaVersion}' != '{SchemaVersion}'.");
        if (doc.Venue != Venue)
            throw new InvalidDataException($"Market calendar venue '{doc.Venue}' != '{Venue}'.");
        if (doc.Coverage is null
            || !DateOnly.TryParseExact(doc.Coverage.FromDate, "yyyy-MM-dd", out var from)
            || !DateOnly.TryParseExact(doc.Coverage.ToDate, "yyyy-MM-dd", out var to)
            || to < from)
            throw new InvalidDataException("Market calendar coverage must be {fromDate, toDate} yyyy-MM-dd with toDate >= fromDate.");
        if (doc.Days is null) throw new InvalidDataException("Market calendar 'days' is missing.");

        var entries = new List<MarketCalendarEntry>(doc.Days.Count);
        var seen = new HashSet<DateOnly>();
        foreach (var day in doc.Days)
        {
            if (day is null || !DateOnly.TryParseExact(day.Date, "yyyy-MM-dd", out var date))
                throw new InvalidDataException($"Market calendar day has invalid date '{day?.Date}'.");
            if (!seen.Add(date)) throw new InvalidDataException($"Market calendar day {date:yyyy-MM-dd} is duplicated.");
            if (date < from || date > to)
                throw new InvalidDataException($"Market calendar day {date:yyyy-MM-dd} is outside coverage.");
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                throw new InvalidDataException($"Market calendar day {date:yyyy-MM-dd} is a weekend (weekends are implicit).");
            if (day.Kind is not (MarketCalendarKinds.MarketHoliday or MarketCalendarKinds.MarketClosure))
                throw new InvalidDataException($"Market calendar day {date:yyyy-MM-dd} has invalid kind '{day.Kind}'.");
            if (string.IsNullOrWhiteSpace(day.Reason))
                throw new InvalidDataException($"Market calendar day {date:yyyy-MM-dd} has no reason.");
            switch (day.Verification)
            {
                case MarketCalendarVerification.Verified:
                    if (string.IsNullOrWhiteSpace(day.SourceUrl)
                        || !Uri.TryCreate(day.SourceUrl, UriKind.Absolute, out var uri)
                        || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                        || string.IsNullOrWhiteSpace(day.SourceTitle))
                        throw new InvalidDataException(
                            $"Verified market calendar day {date:yyyy-MM-dd} needs an http(s) sourceUrl and a sourceTitle.");
                    break;
                case MarketCalendarVerification.UnverifiedLegacy:
                    break;
                default:
                    throw new InvalidDataException(
                        $"Market calendar day {date:yyyy-MM-dd} has invalid verification '{day.Verification}'.");
            }
            entries.Add(new MarketCalendarEntry(date, day.Kind, day.Reason.Trim(),
                string.IsNullOrWhiteSpace(day.SourceUrl) ? null : day.SourceUrl.Trim(),
                string.IsNullOrWhiteSpace(day.SourceTitle) ? null : day.SourceTitle.Trim(),
                day.Verification));
        }

        var version = string.IsNullOrWhiteSpace(doc.CalendarVersion) ? SchemaVersion : doc.CalendarVersion.Trim();
        return new MarketCalendarSource(version, from, to, entries.OrderBy(e => e.Date).ToArray(), [], filePath);
    }
}
