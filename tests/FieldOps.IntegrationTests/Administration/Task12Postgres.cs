using FieldOps.IntegrationTests.Infrastructure;

using Npgsql;

namespace FieldOps.IntegrationTests.Administration;

internal sealed class Task12Postgres(PostgresFixture fixture)
{
    private readonly List<string> _connectionStrings = [];

    public async Task<string> CreateEmptyDatabaseAsync()
    {
        NpgsqlConnectionStringBuilder connectionString = new(await fixture.CreateEmptyDatabaseAsync())
        {
            Pooling = false
        };
        _connectionStrings.Add(connectionString.ConnectionString);
        return connectionString.ConnectionString;
    }

    public async Task AssertNoDatabaseActivityAsync()
    {
        foreach (string connectionString in _connectionStrings)
        {
            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(
                """
                SELECT
                    count(*) FILTER (WHERE pid <> pg_backend_pid()),
                    count(*) FILTER (WHERE pid <> pg_backend_pid() AND state = 'idle in transaction')
                FROM pg_stat_activity
                WHERE datname = current_database()
                """,
                connection);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            long otherConnections = reader.GetInt64(0);
            long idleInTransaction = reader.GetInt64(1);
            if (otherConnections != 0 || idleInTransaction != 0)
            {
                throw new InvalidOperationException(
                    $"Task 12 database activity leaked: connections={otherConnections}, idle-in-transaction={idleInTransaction}.");
            }
        }
    }
}