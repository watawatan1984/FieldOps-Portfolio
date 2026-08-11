using FieldOps.Domain.Entities;
using FieldOps.Features.Abstractions;
using FieldOps.Infrastructure.Auditing;
using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace FieldOps.IntegrationTests.Persistence;

[Collection(DatabaseCollection.Name)]
public sealed class MutationExecutorTests(PostgresFixture postgres)
{
    [Fact]
    public async Task MutationWaitsForSharedAdvisoryLockBeforeOpeningTheBusinessAction()
    {
        string applicationName = $"fieldops-mutation-{Guid.NewGuid():N}";
        string connectionString = new NpgsqlConnectionStringBuilder(await postgres.CreateEmptyDatabaseAsync())
        {
            ApplicationName = applicationName
        }.ConnectionString;

        await using FieldOpsDbContext dbContext = CreateContext(connectionString);
        await dbContext.Database.MigrateAsync();
        MutationExecutor executor = new(dbContext, NullLogger<MutationExecutor>.Instance);

        await using NpgsqlConnection blocker = new(connectionString);
        await blocker.OpenAsync();
        await using NpgsqlTransaction blockerTransaction = await blocker.BeginTransactionAsync();
        await using (NpgsqlCommand takeExclusiveLock = new("SELECT pg_advisory_xact_lock(4601101)", blocker, blockerTransaction))
        {
            await takeExclusiveLock.ExecuteNonQueryAsync();
        }

        TaskCompletionSource actionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> mutation = executor.ExecuteAsync(
            "create-test-branch",
            async cancellationToken =>
            {
                Assert.NotNull(dbContext.Database.CurrentTransaction);
                Assert.True(await CurrentSessionHasSharedAdvisoryLockAsync(dbContext, cancellationToken));
                dbContext.Branches.Add(Branch.Create("Fictional Lock Ordered Branch"));
                actionStarted.SetResult();
                return 17;
            });

        await WaitForAdvisoryLockWaitAsync(blocker, applicationName);
        Assert.False(actionStarted.Task.IsCompleted);

        await blockerTransaction.CommitAsync();

        Assert.Equal(17, await mutation);
        await actionStarted.Task;

        await using FieldOpsDbContext assertContext = CreateContext(connectionString);
        Assert.True(await assertContext.Branches.AnyAsync(branch => branch.Name == "Fictional Lock Ordered Branch"));
    }

    [Fact]
    public async Task ExceptionRollsBackBusinessDataAndAuditSavedInsideTheMutation()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsDbContext dbContext = CreateContext(connectionString);
        await dbContext.Database.MigrateAsync();
        MutationExecutor executor = new(dbContext, NullLogger<MutationExecutor>.Instance);
        AuditWriter auditWriter = new(dbContext, new TestCurrentUser("test-user-42", "System Administrator"), TimeProvider.System);
        Branch branch = Branch.Create("Fictional Rolled Back Branch");

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync<int>(
            "rollback-test-branch",
            async cancellationToken =>
            {
                dbContext.Branches.Add(branch);
                auditWriter.Write(nameof(Branch), branch.Id, "Created");
                await dbContext.SaveChangesAsync(cancellationToken);
                throw new InvalidOperationException("Failure after both rows were written.");
            }));

        await using FieldOpsDbContext assertContext = CreateContext(connectionString);
        Assert.False(await assertContext.Branches.AnyAsync(item => item.Id == branch.Id));
        Assert.False(await assertContext.AuditEntries.AnyAsync(item => item.AggregateId == branch.Id));
    }

    [Fact]
    public async Task MutationLogReportsOperationOutcomeAndDatabaseElapsedMilliseconds()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsDbContext dbContext = CreateContext(connectionString);
        await dbContext.Database.MigrateAsync();
        CapturingLogger<MutationExecutor> logger = new();
        MutationExecutor executor = new(dbContext, logger);

        await executor.ExecuteAsync("diagnostic-db-operation", _ => Task.FromResult(1));

        IReadOnlyDictionary<string, object?> entry = Assert.Single(logger.Entries);
        Assert.Equal("diagnostic-db-operation", entry["Operation"]);
        Assert.Equal("success", entry["Outcome"]);
        Assert.IsType<long>(entry["DbElapsedMs"]);
    }

    private static FieldOpsDbContext CreateContext(string connectionString)
    {
        DbContextOptions<FieldOpsDbContext> options = new DbContextOptionsBuilder<FieldOpsDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new FieldOpsDbContext(options);
    }

    private static async Task<bool> CurrentSessionHasSharedAdvisoryLockAsync(
        FieldOpsDbContext dbContext,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        await using NpgsqlCommand command = new(
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_locks
                WHERE locktype = 'advisory'
                  AND pid = pg_backend_pid()
                  AND mode = 'ShareLock'
                  AND granted
                  AND classid = 0
                  AND objid = 4601101)
            """,
            connection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task WaitForAdvisoryLockWaitAsync(NpgsqlConnection connection, string applicationName)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            await using NpgsqlCommand command = new(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE application_name = @application_name
                      AND wait_event_type = 'Lock'
                      AND wait_event = 'advisory')
                """,
                connection);
            command.Parameters.AddWithValue("application_name", applicationName);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token) ?? false))
            {
                return;
            }

            await Task.Delay(25, timeout.Token);
        }

        throw new TimeoutException("The mutation connection never waited for the advisory lock.");
    }

    private sealed record TestCurrentUser(string UserId, string Role) : ICurrentUser;

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<IReadOnlyDictionary<string, object?>> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                Entries.Add(properties
                    .Where(item => item.Key != "{OriginalFormat}")
                    .ToDictionary(item => item.Key, item => item.Value));
            }
        }
    }
}