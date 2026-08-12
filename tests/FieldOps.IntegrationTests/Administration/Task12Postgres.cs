using FieldOps.IntegrationTests.Infrastructure;

using Npgsql;

namespace FieldOps.IntegrationTests.Administration;

internal sealed class Task12Postgres(PostgresFixture fixture)
{
    private static readonly TimeSpan ActivityDeadline = TimeSpan.FromSeconds(5);
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
            using CancellationTokenSource deadline = new(ActivityDeadline);
            CancellationToken cancellationToken = deadline.Token;
            List<string> remainingConnections = [];
            await using NpgsqlConnection connection = new(connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).WaitAsync(cancellationToken);
                await using NpgsqlCommand command = new(
                    """
                    SELECT pid, state, COALESCE(application_name, ''),
                           COALESCE(wait_event_type, ''), COALESCE(wait_event, ''), COALESCE(md5(query), '')
                    FROM pg_stat_activity
                    WHERE datname = current_database() AND pid <> pg_backend_pid()
                    ORDER BY pid
                    """,
                    connection)
                {
                    CommandTimeout = 5
                };

                while (true)
                {
                    remainingConnections.Clear();
                    await using (NpgsqlDataReader reader =
                        await command.ExecuteReaderAsync(cancellationToken).WaitAsync(cancellationToken))
                    {
                        while (await reader.ReadAsync(cancellationToken).WaitAsync(cancellationToken))
                        {
                            remainingConnections.Add(
                                $"pid={reader.GetInt32(0)},state={reader.GetString(1)}," +
                                $"application={reader.GetString(2)},wait={reader.GetString(3)}/{reader.GetString(4)}," +
                                $"queryHash={reader.GetString(5)}");
                        }
                    }

                    if (remainingConnections.Count == 0)
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "Task 12 database activity failed to reach zero before the absolute 5-second deadline; " +
                    $"last safe diagnostics: {string.Join(";", remainingConnections)}");
            }
        }
    }
}