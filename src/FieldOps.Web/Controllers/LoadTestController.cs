using System.Data;

using FieldOps.Infrastructure.Demo;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Web.Controllers;

[ApiController]
[AllowAnonymous]
[Route("__load-test")]
public sealed class LoadTestController(
    FieldOpsDbContext dbContext,
    DemoDataSeeder dataSeeder,
    IDemoModeVerifier demoModeVerifier) : ControllerBase
{
    private const int MaximumSeededVirtualUsers = 200;
    private static readonly Guid LoadBranchId = DemoDataManifest.Branches[0].Id;

    [HttpPost("preflight")]
    public async Task<IActionResult> Preflight([FromQuery] int vus = 1, CancellationToken cancellationToken = default)
    {
        int normalizedVirtualUsers = Math.Clamp(vus, 1, MaximumSeededVirtualUsers);
        await EnsureDeterministicDatasetAsync(cancellationToken);
        await SeedVirtualUserRecordsAsync(normalizedVirtualUsers, cancellationToken);
        LoadTestCounts counts = await ReadCountsAsync(cancellationToken);
        LoadTestIntegrity integrity = await ReadIntegrityAsync(cancellationToken);
        int activeResetCount = await dbContext.DemoResetExecutions
            .CountAsync(item => item.State == DemoResetState.Running, cancellationToken);
        bool approved = await demoModeVerifier.IsDatabaseApprovedAsync(cancellationToken);
        bool roleLoginReady = await dbContext.Users
            .CountAsync(user => DemoDataManifest.UsersByRole.Values
                .Select(definition => definition.Id)
                .Contains(user.Id), cancellationToken) == DemoDataManifest.DemoUserCount;

        return Ok(new LoadTestPreflightResult(
            Ready: approved && roleLoginReady && activeResetCount == 0 && integrity.Passed,
            RoleLoginReady: roleLoginReady,
            ActiveResetCount: activeResetCount,
            SeededVirtualUsers: normalizedVirtualUsers,
            Counts: counts,
            Integrity: integrity));
    }

    [HttpPost("write/{virtualUser:int}")]
    public async Task<IActionResult> Write(int virtualUser, CancellationToken cancellationToken)
    {
        if (virtualUser is < 1 or > MaximumSeededVirtualUsers)
        {
            return BadRequest(new { error = "virtualUser must be between 1 and 200." });
        }

        Guid partyId = LoadPartyId(virtualUser);
        int changed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "Parties"
            SET "UpdatedAtUtc" = now()
            WHERE "Id" = {partyId}
            """,
            cancellationToken);

        return changed == 1
            ? Ok(new { virtualUser, status = "updated" })
            : NotFound(new { virtualUser });
    }

    [HttpGet("postflight")]
    public async Task<IActionResult> Postflight(CancellationToken cancellationToken)
    {
        LoadTestCounts counts = await ReadCountsAsync(cancellationToken);
        LoadTestIntegrity integrity = await ReadIntegrityAsync(cancellationToken);
        int activeResetCount = await dbContext.DemoResetExecutions
            .CountAsync(item => item.State == DemoResetState.Running, cancellationToken);

        return Ok(new LoadTestPostflightResult(
            Counts: counts,
            Integrity: integrity,
            ActiveResetCount: activeResetCount,
            ResetCount: counts.DemoResetExecutions));
    }

    private async Task SeedVirtualUserRecordsAsync(int virtualUsers, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM "Contacts" WHERE "PartyId" >= '80000000-0000-4000-8000-000000000000'::uuid;
            DELETE FROM "PartyRoles" WHERE "PartyId" >= '80000000-0000-4000-8000-000000000000'::uuid;
            DELETE FROM "PartyBranchAssignments" WHERE "PartyId" >= '80000000-0000-4000-8000-000000000000'::uuid;
            DELETE FROM "Sites" WHERE "PartyId" >= '80000000-0000-4000-8000-000000000000'::uuid;
            DELETE FROM "Parties" WHERE "Id" >= '80000000-0000-4000-8000-000000000000'::uuid;
            """,
            cancellationToken);

        for (int vu = 1; vu <= virtualUsers; vu++)
        {
            Guid partyId = LoadPartyId(vu);
            Guid siteId = LoadSiteId(vu);
            string partyName = "Load Test Customer " + vu.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
            string siteName = "Load Test Site " + vu.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "Parties" ("Id", "OrganizationName", "FirstName", "LastName", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES ({partyId}, {partyName}, NULL, NULL, now(), now());

                INSERT INTO "PartyRoles" ("Id", "PartyId", "RoleType", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES ({LoadRoleId(vu)}, {partyId}, 1, now(), now());

                INSERT INTO "PartyBranchAssignments" ("Id", "PartyId", "BranchId", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES ({LoadAssignmentId(vu)}, {partyId}, {LoadBranchId}, now(), now());

                INSERT INTO "Sites" ("Id", "PartyId", "BranchId", "Name", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES ({siteId}, {partyId}, {LoadBranchId}, {siteName}, now(), now());
                """,
                cancellationToken);
        }
    }

    private async Task EnsureDeterministicDatasetAsync(CancellationToken cancellationToken)
    {
        int partyCount = await dbContext.Parties.CountAsync(cancellationToken);
        int workOrderCount = await dbContext.WorkOrders.CountAsync(cancellationToken);
        if (partyCount >= DemoDataManifest.PartyCount && workOrderCount >= DemoDataManifest.WorkOrderCount)
        {
            return;
        }

        IReadOnlyDictionary<string, string> passwordHashes =
            await dataSeeder.CapturePasswordHashesAsync(cancellationToken);
        await dataSeeder.DeleteDemoOwnedRowsAsync(cancellationToken);
        bool shouldCloseConnection = dbContext.Database.GetDbConnection().State == ConnectionState.Closed;
        if (shouldCloseConnection)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await dataSeeder.SeedAsync(passwordHashes, cancellationToken);
        }
        finally
        {
            if (shouldCloseConnection)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    private async Task<LoadTestCounts> ReadCountsAsync(CancellationToken cancellationToken)
    {
        return new LoadTestCounts(
            Branches: await dbContext.Branches.CountAsync(cancellationToken),
            Parties: await dbContext.Parties.CountAsync(cancellationToken),
            PartyRoles: await dbContext.Set<FieldOps.Domain.Entities.PartyRole>().CountAsync(cancellationToken),
            PartyBranchAssignments: await dbContext.Set<FieldOps.Domain.Entities.PartyBranchAssignment>().CountAsync(cancellationToken),
            Contacts: await dbContext.Set<FieldOps.Domain.Entities.Contact>().CountAsync(cancellationToken),
            Sites: await dbContext.Set<FieldOps.Domain.Entities.Site>().CountAsync(cancellationToken),
            SalesOpportunities: await dbContext.SalesOpportunities.CountAsync(cancellationToken),
            WorkOrders: await dbContext.WorkOrders.CountAsync(cancellationToken),
            WorkEvents: await dbContext.Set<FieldOps.Domain.Entities.WorkEvent>().CountAsync(cancellationToken),
            AuditEntries: await dbContext.AuditEntries.CountAsync(cancellationToken),
            Users: await dbContext.Users.CountAsync(cancellationToken),
            DemoResetExecutions: await dbContext.DemoResetExecutions.CountAsync(cancellationToken));
    }

    private async Task<LoadTestIntegrity> ReadIntegrityAsync(CancellationToken cancellationToken)
    {
        int orphanedRows = await dbContext.Database
            .SqlQueryRaw<int>(
                """
                SELECT
                    (SELECT count(*)::int FROM "PartyRoles" role WHERE NOT EXISTS (SELECT 1 FROM "Parties" party WHERE party."Id" = role."PartyId")) +
                    (SELECT count(*)::int FROM "PartyBranchAssignments" assignment WHERE NOT EXISTS (SELECT 1 FROM "Parties" party WHERE party."Id" = assignment."PartyId")) +
                    (SELECT count(*)::int FROM "Sites" site WHERE NOT EXISTS (SELECT 1 FROM "Parties" party WHERE party."Id" = site."PartyId")) +
                    (SELECT count(*)::int FROM "SalesOpportunities" sale WHERE NOT EXISTS (SELECT 1 FROM "Parties" party WHERE party."Id" = sale."PartyId")) +
                    (SELECT count(*)::int FROM "WorkOrders" work WHERE NOT EXISTS (SELECT 1 FROM "Parties" party WHERE party."Id" = work."PartyId")) +
                    (SELECT count(*)::int FROM "WorkEvents" event WHERE NOT EXISTS (SELECT 1 FROM "WorkOrders" work WHERE work."Id" = event."WorkOrderId")) AS "Value"
                """)
            .SingleAsync(cancellationToken);

        return new LoadTestIntegrity(orphanedRows == 0, orphanedRows);
    }

    private static Guid LoadPartyId(int number) => NumberedGuid("80000000-0000-4000-8000-", number);

    private static Guid LoadRoleId(int number) => NumberedGuid("81000000-0000-4000-8000-", number);

    private static Guid LoadAssignmentId(int number) => NumberedGuid("82000000-0000-4000-8000-", number);

    private static Guid LoadSiteId(int number) => NumberedGuid("83000000-0000-4000-8000-", number);

    private static Guid NumberedGuid(string prefix, int number) =>
        Guid.Parse($"{prefix}{number:000000000000}");
}

public sealed record LoadTestPreflightResult(
    bool Ready,
    bool RoleLoginReady,
    int ActiveResetCount,
    int SeededVirtualUsers,
    LoadTestCounts Counts,
    LoadTestIntegrity Integrity);

public sealed record LoadTestPostflightResult(
    LoadTestCounts Counts,
    LoadTestIntegrity Integrity,
    int ActiveResetCount,
    int ResetCount);

public sealed record LoadTestCounts(
    int Branches,
    int Parties,
    int PartyRoles,
    int PartyBranchAssignments,
    int Contacts,
    int Sites,
    int SalesOpportunities,
    int WorkOrders,
    int WorkEvents,
    int AuditEntries,
    int Users,
    int DemoResetExecutions);

public sealed record LoadTestIntegrity(bool Passed, int OrphanedRows);