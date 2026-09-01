using FieldOps.Domain.Entities;
using FieldOps.Features.Administration;
using FieldOps.Infrastructure.Demo;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace FieldOps.IntegrationTests.Administration;

[Collection(DatabaseCollection.Name)]
public sealed class DemoResetConcurrencyTests(PostgresFixture fixture) : IAsyncLifetime
{
    private Task12Postgres postgres { get; } = new(fixture);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => postgres.AssertNoDatabaseActivityAsync();

    [Fact]
    public async Task ConcurrentSameKeyWaitsThenReturnsTheStoredCompletion()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        OneShotBlockingObserver observer = new(DemoResetPhase.BeforeCommit);
        await using FieldOpsWebApplicationFactory application = CreateApplication(connectionString, observer);
        using HttpClient startup = application.CreateClient();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        DemoResetCommand firstCommand = Command("concurrent-same-key", "concurrent-same-first");
        Task<DemoResetResult> first = RunResetAsync(application.Services, firstCommand, timeout.Token);
        Task<DemoResetResult>? second = null;

        try
        {
            await observer.Reached.WaitAsync(timeout.Token);
            second = RunResetAsync(
                application.Services,
                firstCommand with { CorrelationId = "concurrent-same-second" },
                timeout.Token);
            await WaitForAdvisoryWaitAsync(connectionString, "ExclusiveLock", timeout.Token);
            Assert.False(second.IsCompleted);
        }
        catch
        {
            timeout.Cancel();
            observer.Release();
            await ObserveCompletionAsync(first, second);
            throw;
        }
        finally
        {
            observer.Release();
        }

        DemoResetResult firstResult = await first.WaitAsync(timeout.Token);
        DemoResetResult secondResult = await (second ?? throw new InvalidOperationException("Second reset did not start."))
            .WaitAsync(timeout.Token);
        Assert.False(firstResult.WasAlreadyCompleted);
        Assert.True(secondResult.WasAlreadyCompleted);
        Assert.Equal(firstResult.CorrelationId, secondResult.CorrelationId);

        await using AsyncServiceScope assertScope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = assertScope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Equal(1, await dbContext.DemoResetExecutions.CountAsync(item =>
            item.IdempotencyKey == firstCommand.IdempotencyKey));
    }

    [Fact]
    public async Task ConcurrentDifferentKeyWaitsThenExecutesASecondDeterministicReset()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        OneShotBlockingObserver observer = new(DemoResetPhase.BeforeCommit);
        await using FieldOpsWebApplicationFactory application = CreateApplication(connectionString, observer);
        using HttpClient startup = application.CreateClient();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Task<DemoResetResult> first = RunResetAsync(
            application.Services,
            Command("concurrent-first-key", "concurrent-first-correlation"),
            timeout.Token);
        Task<DemoResetResult>? second = null;

        try
        {
            await observer.Reached.WaitAsync(timeout.Token);
            second = RunResetAsync(
                application.Services,
                Command("concurrent-second-key", "concurrent-second-correlation"),
                timeout.Token);
            await WaitForAdvisoryWaitAsync(connectionString, "ExclusiveLock", timeout.Token);
            Assert.False(second.IsCompleted);
        }
        catch
        {
            timeout.Cancel();
            observer.Release();
            await ObserveCompletionAsync(first, second);
            throw;
        }
        finally
        {
            observer.Release();
        }

        Assert.False((await first.WaitAsync(timeout.Token)).WasAlreadyCompleted);
        Assert.False((await (second ?? throw new InvalidOperationException("Second reset did not start."))
            .WaitAsync(timeout.Token)).WasAlreadyCompleted);

        await using AsyncServiceScope assertScope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = assertScope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Equal(2, await dbContext.DemoResetExecutions.CountAsync());
        Assert.Equal(DemoDataManifest.BranchCount, await dbContext.Branches.CountAsync());
        Assert.Equal(DemoDataManifest.QuoteCount, await dbContext.Quotes.CountAsync());
        Assert.Equal(DemoDataManifest.WorkEventCount, await dbContext.Set<FieldOps.Domain.Entities.WorkEvent>().CountAsync());
        Assert.True(await dbContext.Parties.AnyAsync(item => item.Id == DemoDataManifest.PartyId(1)));
    }

    [Fact]
    public async Task NormalMutationBlocksBehindResetThenCommitsWithAuditWithoutLosingItsWrite()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        OneShotBlockingObserver observer = new(DemoResetPhase.BeforeCommit);
        await using FieldOpsWebApplicationFactory application = CreateApplication(connectionString, observer);
        using HttpClient startup = application.CreateClient();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Task<DemoResetResult> reset = RunResetAsync(
            application.Services,
            Command("reset-before-mutation", "reset-before-mutation-correlation"),
            timeout.Token);
        Task<Guid>? mutation = null;

        try
        {
            await observer.Reached.WaitAsync(timeout.Token);
            mutation = RunBranchMutationAsync(application.Services, timeout.Token);
            await WaitForAdvisoryWaitAsync(connectionString, "ShareLock", timeout.Token);
            Assert.False(mutation.IsCompleted);
        }
        catch
        {
            timeout.Cancel();
            observer.Release();
            await ObserveCompletionAsync(reset, mutation);
            throw;
        }
        finally
        {
            observer.Release();
        }

        await reset.WaitAsync(timeout.Token);
        Guid branchId = await (mutation ?? throw new InvalidOperationException("Mutation did not start."))
            .WaitAsync(timeout.Token);
        await using AsyncServiceScope assertScope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = assertScope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.True(await dbContext.Branches.AnyAsync(branch => branch.Id == branchId));
        Assert.True(await dbContext.AuditEntries.AnyAsync(audit =>
            audit.AggregateId == branchId && audit.Action == "PostResetMutation"));
        Assert.Equal(DemoDataManifest.BranchCount + 1, await dbContext.Branches.CountAsync());
    }

    [Fact]
    public async Task ResetWaitsBehindAnExistingNormalMutationSharedLock()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient startup = application.CreateClient();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        TaskCompletionSource mutationHasLock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseMutation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Guid> mutation = RunHeldMutationAsync(
            application.Services,
            mutationHasLock,
            releaseMutation,
            timeout.Token);
        Task<DemoResetResult>? reset = null;

        try
        {
            await mutationHasLock.Task.WaitAsync(timeout.Token);
            reset = RunResetAsync(
                application.Services,
                Command("reset-after-mutation", "reset-after-mutation-correlation"),
                timeout.Token);
            await WaitForAdvisoryWaitAsync(connectionString, "ExclusiveLock", timeout.Token);
            Assert.False(reset.IsCompleted);
        }
        catch
        {
            timeout.Cancel();
            releaseMutation.TrySetResult();
            await ObserveCompletionAsync(mutation, reset);
            throw;
        }
        finally
        {
            releaseMutation.TrySetResult();
        }

        Guid mutationBranchId = await mutation.WaitAsync(timeout.Token);
        await (reset ?? throw new InvalidOperationException("Reset did not start."))
            .WaitAsync(timeout.Token);
        await using AsyncServiceScope assertScope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = assertScope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.False(await dbContext.Branches.AnyAsync(branch => branch.Id == mutationBranchId));
        Assert.Equal(DemoDataManifest.BranchCount, await dbContext.Branches.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovedMarkerRowIsLockedUntilTheResetTransactionCommits(bool deleteMarker)
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        MarkerRowLockObserver observer = new(connectionString, deleteMarker);
        await using FieldOpsWebApplicationFactory application = CreateApplication(connectionString, observer);
        using HttpClient startup = application.CreateClient();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Task<DemoResetResult> reset = RunResetAsync(
            application.Services,
            Command(
                deleteMarker ? "marker-row-delete-lock" : "marker-row-update-lock",
                deleteMarker ? "marker-row-delete-correlation" : "marker-row-update-correlation"),
            timeout.Token);

        try
        {
            int updateBackendPid = await observer.UpdateBackendPid.WaitAsync(timeout.Token);
            await observer.BeforeCommit.WaitAsync(timeout.Token);
            await WaitForBackendLockWaitAsync(connectionString, updateBackendPid, timeout.Token);
            Assert.False(observer.UpdateTask?.IsCompleted ?? true);
        }
        catch
        {
            observer.ReleaseReset();
            timeout.Cancel();
            await ObserveCompletionAsync(reset, observer.UpdateTask);
            throw;
        }
        finally
        {
            observer.ReleaseReset();
        }

        await reset.WaitAsync(timeout.Token);
        await (observer.UpdateTask ?? throw new InvalidOperationException("Marker update did not start."))
            .WaitAsync(timeout.Token);
        await using AsyncServiceScope assertScope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = assertScope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.True(await dbContext.DemoDatasetMarkers.AnyAsync(marker =>
            marker.Id == DemoDataManifest.DatasetMarkerId &&
            marker.DatasetIdentifier == DemoModeOptions.ApprovedDatasetIdentifier &&
            marker.DatasetVersion == DemoModeOptions.ApprovedDatasetVersion));
    }

    private static FieldOpsWebApplicationFactory CreateApplication(
        string connectionString,
        IDemoResetPhaseObserver observer) =>
        new(
            connectionString,
            services =>
            {
                services.RemoveAll<IDemoResetPhaseObserver>();
                services.AddSingleton<IDemoResetPhaseObserver>(observer);
            });

    private static DemoResetCommand Command(string key, string correlationId) =>
        new(
            key,
            DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
            correlationId);

    private static async Task<DemoResetResult> RunResetAsync(
        IServiceProvider services,
        DemoResetCommand command,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDemoResetService>()
            .ResetAsync(command, cancellationToken);
    }

    private static async Task<Guid> RunBranchMutationAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        MutationExecutor executor = new(dbContext, NullLogger<MutationExecutor>.Instance);
        Branch branch = Branch.Create("Fictional post-reset mutation branch");
        return await executor.ExecuteAsync(
            "post-reset-branch",
            _ =>
            {
                dbContext.Branches.Add(branch);
                dbContext.AuditEntries.Add(new AuditEntry(
                    nameof(Branch),
                    branch.Id,
                    "PostResetMutation",
                    DateTime.UtcNow,
                    DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id));
                return Task.FromResult(branch.Id);
            },
            cancellationToken);
    }

    private static async Task<Guid> RunHeldMutationAsync(
        IServiceProvider services,
        TaskCompletionSource mutationHasLock,
        TaskCompletionSource releaseMutation,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        MutationExecutor executor = new(dbContext, NullLogger<MutationExecutor>.Instance);
        Branch branch = Branch.Create("Fictional held mutation branch");
        return await executor.ExecuteAsync(
            "held-reset-coordination-branch",
            async actionCancellationToken =>
            {
                mutationHasLock.TrySetResult();
                await releaseMutation.Task.WaitAsync(actionCancellationToken);
                dbContext.Branches.Add(branch);
                return branch.Id;
            },
            cancellationToken);
    }

    private static async Task WaitForAdvisoryWaitAsync(
        string connectionString,
        string lockMode,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        while (true)
        {
            await using NpgsqlCommand command = new(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_locks
                    WHERE locktype = 'advisory'
                      AND mode = @lockMode
                      AND NOT granted
                      AND classid = 0
                      AND objid = 4601101)
                """,
                connection);
            command.Parameters.AddWithValue("lockMode", lockMode);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false))
            {
                return;
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private static async Task WaitForBackendLockWaitAsync(
        string connectionString,
        int backendPid,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        while (true)
        {
            await using NpgsqlCommand command = new(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_locks
                    WHERE pid = @backendPid
                      AND NOT granted)
                """,
                connection);
            command.Parameters.AddWithValue("backendPid", backendPid);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false))
            {
                return;
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private static async Task ObserveCompletionAsync(params Task?[] tasks)
    {
        foreach (Task task in tasks.OfType<Task>())
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Cleanup deliberately observes every task; the original assertion/failure is preserved.
            }
        }
    }

    private sealed class OneShotBlockingObserver(DemoResetPhase phase) : IDemoResetPhaseObserver
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked;

        public Task Reached => _reached.Task;

        public async Task ObserveAsync(DemoResetPhase observedPhase, CancellationToken cancellationToken)
        {
            if (observedPhase != phase || Interlocked.CompareExchange(ref _blocked, 1, 0) != 0)
            {
                return;
            }

            _reached.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class MarkerRowLockObserver(
        string connectionString,
        bool deleteMarker) : IDemoResetPhaseObserver
    {
        private readonly TaskCompletionSource<int> _updateBackendPid =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _beforeCommit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseReset =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _updateStarted;

        public Task<int> UpdateBackendPid => _updateBackendPid.Task;

        public Task BeforeCommit => _beforeCommit.Task;

        public Task? UpdateTask { get; private set; }

        public async Task ObserveAsync(DemoResetPhase phase, CancellationToken cancellationToken)
        {
            if (phase == DemoResetPhase.MarkerLocked &&
                Interlocked.CompareExchange(ref _updateStarted, 1, 0) == 0)
            {
                UpdateTask = UpdateMarkerAndRollbackAsync(cancellationToken);
                await _updateBackendPid.Task.WaitAsync(cancellationToken);
            }

            if (phase == DemoResetPhase.BeforeCommit)
            {
                _beforeCommit.TrySetResult();
                await _releaseReset.Task.WaitAsync(cancellationToken);
            }
        }

        public void ReleaseReset()
        {
            _releaseReset.TrySetResult();
        }

        private async Task UpdateMarkerAndRollbackAsync(CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            _updateBackendPid.TrySetResult(connection.ProcessID);
            await using NpgsqlCommand command = new(
                deleteMarker
                    ? "DELETE FROM \"DemoDatasetMarkers\""
                    : "UPDATE \"DemoDatasetMarkers\" SET \"DatasetVersion\" = 'blocked-update'",
                connection,
                transaction);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
            await transaction.RollbackAsync(cancellationToken);
        }
    }
}