using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Npgsql;

namespace FieldOps.IntegrationTests.Authorization;

[Collection(DatabaseCollection.Name)]
public sealed class AuthorizationMigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task IdempotentMigrationSqlAppliesTwiceWithoutDrift()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        DbContextOptions<FieldOpsDbContext> options = new DbContextOptionsBuilder<FieldOpsDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using FieldOpsDbContext context = new(options);
        string script = context.Database.GetService<IMigrator>().GenerateScript(
            options: MigrationsSqlGenerationOptions.Idempotent);

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(script, connection);
        await command.ExecuteNonQueryAsync();
        await command.ExecuteNonQueryAsync();

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Contains(
            await context.Database.GetAppliedMigrationsAsync(),
            migration => migration.EndsWith("_AddDemoIdentity", StringComparison.Ordinal));
    }
}
