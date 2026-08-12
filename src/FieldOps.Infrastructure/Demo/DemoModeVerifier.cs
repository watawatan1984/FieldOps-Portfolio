using FieldOps.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FieldOps.Infrastructure.Demo;

public interface IDemoModeVerifier
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<bool> IsApprovedAsync(CancellationToken cancellationToken = default);

    Task<bool> IsDatabaseApprovedAsync(CancellationToken cancellationToken = default);

    Task EnsureApprovedAndLockMarkerAsync(CancellationToken cancellationToken = default);

    Task EnsureApprovedAsync(CancellationToken cancellationToken = default);
}

public sealed class DemoModeApprovalState
{
    private int _approval;

    public bool IsInitialized => Volatile.Read(ref _approval) != 0;

    public bool IsApproved => Volatile.Read(ref _approval) == 2;

    public void Initialize(bool approved)
    {
        Interlocked.CompareExchange(ref _approval, approved ? 2 : 1, 0);
    }
}

public sealed class DemoModeVerifier(
    FieldOpsDbContext dbContext,
    IOptions<DemoModeOptions> options,
    DemoModeApprovalState approvalState,
    ILogger<DemoModeVerifier> logger) : IDemoModeVerifier
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!approvalState.IsInitialized)
        {
            approvalState.Initialize(await CheckDatabaseAsync(cancellationToken));
        }
    }

    public Task<bool> IsApprovedAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(options.Value.Enabled && approvalState.IsApproved);
    }

    public Task<bool> IsDatabaseApprovedAsync(CancellationToken cancellationToken = default)
    {
        return CheckDatabaseAsync(cancellationToken);
    }

    public async Task EnsureApprovedAndLockMarkerAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled || !approvalState.IsApproved || !await LockApprovedMarkerAsync(cancellationToken))
        {
            throw new DemoModeUnavailableException();
        }
    }

    private async Task<bool> CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return false;
        }

        try
        {
            return await dbContext.DemoDatasetMarkers.AsNoTracking().AnyAsync(
                marker =>
                    marker.Id == DemoDataManifest.DatasetMarkerId &&
                    marker.DatasetIdentifier == DemoModeOptions.ApprovedDatasetIdentifier &&
                    marker.DatasetVersion == DemoModeOptions.ApprovedDatasetVersion,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Demo dataset marker verification failed closed with safe type {ExceptionType}",
                exception.GetType().Name);
            return false;
        }
    }

    public async Task EnsureApprovedAsync(CancellationToken cancellationToken = default)
    {
        if (!approvalState.IsApproved || !await CheckDatabaseAsync(cancellationToken))
        {
            throw new DemoModeUnavailableException();
        }
    }

    private async Task<bool> LockApprovedMarkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            DemoDatasetMarker? marker = await dbContext.DemoDatasetMarkers
                .FromSqlInterpolated(
                    $"""
                    SELECT "Id", "DatasetIdentifier", "DatasetVersion", "InstalledAtUtc"
                    FROM "DemoDatasetMarkers"
                    WHERE "Id" = {DemoDataManifest.DatasetMarkerId}
                      AND "DatasetIdentifier" = {DemoModeOptions.ApprovedDatasetIdentifier}
                      AND "DatasetVersion" = {DemoModeOptions.ApprovedDatasetVersion}
                    FOR SHARE
                    """)
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
            return marker is not null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Demo dataset marker lock verification failed closed with safe type {ExceptionType}",
                exception.GetType().Name);
            return false;
        }
    }
}

public sealed class DemoModeUnavailableException()
    : InvalidOperationException("Demo mode is unavailable because its approved configuration and database marker do not match.");