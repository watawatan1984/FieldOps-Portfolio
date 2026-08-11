using Npgsql;

using Testcontainers.PostgreSql;

namespace FieldOps.IntegrationTests.Infrastructure;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("postgres")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ImageName => "postgres:17-alpine";

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<string> CreateEmptyDatabaseAsync()
    {
        string databaseName = $"fieldops_{Guid.NewGuid():N}";

        await using NpgsqlConnection connection = new(_container.GetConnectionString());
        await connection.OpenAsync();

        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();

        NpgsqlConnectionStringBuilder connectionString = new(_container.GetConnectionString())
        {
            Database = databaseName
        };

        return connectionString.ConnectionString;
    }
}