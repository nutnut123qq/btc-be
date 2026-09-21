using System.Data;
using System.Data.Common;
using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public interface IPaperObservationReader
{
    Task<PaperObservationListResponse> GetLatestAsync(string symbol, int take, CancellationToken cancellationToken);
}

public sealed class PaperObservationReader(AppDbContext db, ProductionSymbolPolicy symbolPolicy) : IPaperObservationReader
{
    public async Task<PaperObservationListResponse> GetLatestAsync(
        string symbol,
        int take,
        CancellationToken cancellationToken)
    {
        symbol = symbolPolicy.EnsureActive(symbol);
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(cancellationToken);
        try
        {
            if (!await TableExistsAsync(connection, cancellationToken))
                return new(symbol, false, "Forward observation recorder has not initialized its append-only table.", []);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "Id", "DecisionId", "RecorderVersion", "Symbol", "Timeframe",
                       "SignalBarOpenTimeMs", "SignalBarCloseTimeMs", "ObservedAtUtc", "AvailableTimeMs",
                       "ModelVersion", "Decision", "Confidence", "AbstentionReason",
                       "QuoteSource", "QuotePrice", "QuoteReceivedAtUtc", "QuoteReceivedTimeMs",
                       "ConfigProvenanceJson"::text, "EvidenceProvenanceJson"::text,
                       "FillPrice", "FillObservedAtUtc", "OutcomeReturn", "OutcomeObservedAtUtc", "OutcomeHorizon"
                FROM "PaperObservations"
                WHERE "Symbol" = @symbol
                ORDER BY "SignalBarCloseTimeMs" DESC
                LIMIT @take
                """;
            AddParameter(command, "symbol", symbol);
            AddParameter(command, "take", Math.Clamp(take, 1, 200));
            var items = new List<PaperObservationDto>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new PaperObservationDto(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetInt64(5), reader.GetInt64(6), reader.GetDateTime(7), reader.GetInt64(8),
                    GetNullableString(reader, 9), reader.GetString(10), GetNullableDouble(reader, 11),
                    GetNullableString(reader, 12), reader.GetString(13), GetNullableDecimal(reader, 14),
                    GetNullableDateTime(reader, 15), GetNullableInt64(reader, 16), reader.GetString(17), reader.GetString(18),
                    GetNullableDecimal(reader, 19), GetNullableDateTime(reader, 20), GetNullableDouble(reader, 21),
                    GetNullableDateTime(reader, 22), GetNullableString(reader, 23)));
            }
            return new(symbol, true, null, items);
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    private static async Task<bool> TableExistsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass('public.\"PaperObservations\"') IS NOT NULL";
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string? GetNullableString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static double? GetNullableDouble(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    private static decimal? GetNullableDecimal(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    private static long? GetNullableInt64(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static DateTime? GetNullableDateTime(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
}
