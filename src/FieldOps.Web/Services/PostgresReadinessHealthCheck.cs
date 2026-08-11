using FieldOps.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FieldOps.Web.Services;

public sealed class PostgresReadinessHealthCheck(FieldOpsDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await dbContext.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.");
            }

            IEnumerable<string> pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
            int pendingCount = pendingMigrations.Count();
            return pendingCount == 0
                ? HealthCheckResult.Healthy("PostgreSQL is reachable and the schema is current.")
                : HealthCheckResult.Unhealthy($"The database has {pendingCount} pending migration(s).");
        }
        catch
        {
            return HealthCheckResult.Unhealthy("PostgreSQL readiness could not be verified.");
        }
    }
}