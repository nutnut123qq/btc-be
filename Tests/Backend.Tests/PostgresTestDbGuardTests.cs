using Npgsql;

namespace Backend.Tests;

/// <summary>
/// The one way a test reads BTC_TEST_POSTGRES. A connection string naming any database other than the throwaway
/// test DB fails the test here, before its first connection, so a mis-pointed variable can never write to
/// bitcoin_analyst or the live work database on the same cluster.
/// </summary>
internal static class PostgresTestDb
{
    public const string DatabaseName = "btc_eqx_test";

    /// <summary>BTC_TEST_POSTGRES after <see cref="Guard"/>; null when unset (Postgres tests then return early).</summary>
    public static string? ConnectionString()
    {
        var cs = Environment.GetEnvironmentVariable("BTC_TEST_POSTGRES");
        return string.IsNullOrWhiteSpace(cs) ? null : Guard(cs);
    }

    /// <summary>Returns <paramref name="connectionString"/> only when its Database is btc_eqx_test or btc_eqx_test_*.</summary>
    public static string Guard(string connectionString)
    {
        // Parsed by the builder Npgsql itself connects with (keyword synonyms, quoting, trimming); compared
        // ordinally because PostgreSQL database names are case-sensitive.
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        if (database == DatabaseName
            || (database?.StartsWith(DatabaseName + "_", StringComparison.Ordinal) == true && database.Length > DatabaseName.Length + 1))
            return connectionString;
        throw new InvalidOperationException(
            $"BTC_TEST_POSTGRES must name database {DatabaseName} (or {DatabaseName}_*), not '{database}'; refusing to connect.");
    }
}

public class PostgresTestDbGuardTests
{
    [Theory]
    [InlineData("Host=localhost;Port=5432;Database=btc_eqx_test;Username=postgres;Password=postgres")]
    [InlineData("Host=localhost;Database=btc_eqx_test_snapfix_0123456789abcdef;Username=postgres")]
    [InlineData("Host=localhost;DB=btc_eqx_test;Username=postgres")] // keyword synonym
    [InlineData("Host=localhost; Database = btc_eqx_test ;Username=postgres")] // unquoted value is trimmed, as Npgsql connects
    public void Guard_AcceptsTheTestDatabase(string cs) => Assert.Same(cs, PostgresTestDb.Guard(cs));

    [Theory]
    [InlineData("Host=localhost;Database=bitcoin_analyst;Username=postgres")]
    [InlineData("Host=localhost;Database=btc_work_srv20261022;Username=postgres")]
    [InlineData("Host=localhost;Username=postgres")] // no Database: Npgsql would fall back to the username
    [InlineData("Host=localhost;Username=btc_eqx_test")]
    [InlineData("Host=localhost;Database=BTC_EQX_TEST;Username=postgres")] // database names are case-sensitive
    [InlineData("Host=localhost;Database=' btc_eqx_test';Username=postgres")] // quoted whitespace is part of the name
    [InlineData("Host=localhost;Database=btc_eqx_test_;Username=postgres")]
    [InlineData("Host=localhost;Database=btc_eqx_testing;Username=postgres")]
    [InlineData("Host=btc_eqx_test.local;Database=bitcoin_analyst;Username=btc_eqx_test;Password=btc_eqx_test")]
    public void Guard_RejectsEveryOtherDatabase(string cs)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PostgresTestDb.Guard(cs));
        Assert.DoesNotContain("Password", ex.Message); // the message names the database, never the connection string
    }
}
