using System.Security.Cryptography;

using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Npgsql;

namespace FieldOps.Infrastructure.Demo;

public sealed class DemoDataSeeder(
    FieldOpsDbContext dbContext,
    IPasswordHasher<ApplicationUser> passwordHasher)
{
    public async Task<IReadOnlyDictionary<string, string>> CapturePasswordHashesAsync(
        CancellationToken cancellationToken = default)
    {
        string[] userIds = DemoDataManifest.UsersByRole.Values.Select(user => user.Id).ToArray();
        return await dbContext.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id) && user.PasswordHash != null)
            .ToDictionaryAsync(user => user.Id, user => user.PasswordHash!, cancellationToken);
    }

    public async Task DeleteDemoOwnedRowsAsync(CancellationToken cancellationToken = default)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            "SELECT set_config('fieldops.allow_historical_delete', txid_current()::text, true)",
            cancellationToken);

        string[] statements =
        [
            "DELETE FROM \"WorkEvents\"",
            "DELETE FROM \"AuditEntries\"",
            "DELETE FROM \"WorkOrders\"",
            "DELETE FROM \"SalesOpportunities\"",
            "DELETE FROM \"AspNetUserClaims\"",
            "DELETE FROM \"AspNetUserLogins\"",
            "DELETE FROM \"AspNetUserTokens\"",
            "DELETE FROM \"AspNetUserRoles\"",
            "DELETE FROM \"AspNetUsers\"",
            "DELETE FROM \"Contacts\"",
            "DELETE FROM \"PartyRoles\"",
            "DELETE FROM \"PartyBranchAssignments\"",
            "DELETE FROM \"Sites\"",
            "DELETE FROM \"Parties\"",
            "DELETE FROM \"Branches\""
        ];

        foreach (string statement in statements)
        {
            await dbContext.Database.ExecuteSqlRawAsync(statement, cancellationToken);
        }
    }

    public async Task SeedAsync(
        IReadOnlyDictionary<string, string> passwordHashes,
        CancellationToken cancellationToken = default)
    {
        await dbContext.Database.ExecuteSqlRawAsync(SeedCoreSql, cancellationToken);
        await SeedUsersAsync(passwordHashes, cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(SeedOperationalSql, cancellationToken);
    }

    private async Task SeedUsersAsync(
        IReadOnlyDictionary<string, string> passwordHashes,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        Dictionary<string, string> roleIds = await dbContext.Roles
            .Where(role => role.Name != null && DemoRoleNames.All.Contains(role.Name))
            .ToDictionaryAsync(role => role.Name!, role => role.Id, cancellationToken);
        foreach ((string role, DemoUser definition) in DemoDataManifest.UsersByRole)
        {
            ApplicationUser passwordSource = new()
            {
                Id = definition.Id,
                UserName = definition.UserName,
                Email = definition.UserName,
                DisplayName = definition.DisplayName,
                BranchId = definition.BranchId
            };
            string passwordHash = passwordHashes.GetValueOrDefault(definition.Id)
                ?? passwordHasher.HashPassword(
                    passwordSource,
                    $"Aa1!{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}");

            await using NpgsqlCommand insertUser = new(
                """
                INSERT INTO "AspNetUsers" (
                    "Id", "DisplayName", "BranchId", "UserName", "NormalizedUserName",
                    "Email", "NormalizedEmail", "EmailConfirmed", "PasswordHash",
                    "SecurityStamp", "ConcurrencyStamp", "PhoneNumber", "PhoneNumberConfirmed",
                    "TwoFactorEnabled", "LockoutEnd", "LockoutEnabled", "AccessFailedCount")
                VALUES (
                    @id, @displayName, @branchId, @userName, @normalizedUserName,
                    @email, @normalizedEmail, TRUE, @passwordHash,
                    @securityStamp, @concurrencyStamp, NULL, FALSE,
                    FALSE, NULL, FALSE, 0)
                """,
                connection,
                (NpgsqlTransaction?)dbContext.Database.CurrentTransaction?.GetDbTransaction());
            insertUser.Parameters.AddWithValue("id", definition.Id);
            insertUser.Parameters.AddWithValue("displayName", definition.DisplayName);
            insertUser.Parameters.AddWithValue("branchId", (object?)definition.BranchId ?? DBNull.Value);
            insertUser.Parameters.AddWithValue("userName", definition.UserName);
            insertUser.Parameters.AddWithValue("normalizedUserName", definition.UserName.ToUpperInvariant());
            insertUser.Parameters.AddWithValue("email", definition.UserName);
            insertUser.Parameters.AddWithValue("normalizedEmail", definition.UserName.ToUpperInvariant());
            insertUser.Parameters.AddWithValue("passwordHash", passwordHash);
            insertUser.Parameters.AddWithValue("securityStamp", definition.SecurityStamp);
            insertUser.Parameters.AddWithValue("concurrencyStamp", definition.ConcurrencyStamp);
            await insertUser.ExecuteNonQueryAsync(cancellationToken);

            await using NpgsqlCommand insertRole = new(
                "INSERT INTO \"AspNetUserRoles\" (\"UserId\", \"RoleId\") VALUES (@userId, @roleId)",
                connection,
                (NpgsqlTransaction?)dbContext.Database.CurrentTransaction?.GetDbTransaction());
            insertRole.Parameters.AddWithValue("userId", definition.Id);
            insertRole.Parameters.AddWithValue(
                "roleId",
                roleIds.GetValueOrDefault(role)
                    ?? throw new InvalidOperationException($"The preserved Identity role definition {role} is missing."));
            await insertRole.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private const string SeedCoreSql =
        """
        INSERT INTO "Branches" ("Id", "Name", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('00000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            (ARRAY[
                'Fictional Central Service Branch',
                'Fictional Field Service Branch',
                'Fictional North Service Branch',
                'Fictional South Service Branch',
                'Fictional West Service Branch'])[i],
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM generate_series(1, 5) AS i;

        INSERT INTO "Parties" ("Id", "OrganizationName", "FirstName", "LastName", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('10000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            'Fictional Service Customer ' || lpad(i::text, 2, '0'),
            NULL,
            NULL,
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM generate_series(1, 40) AS i;

        INSERT INTO "PartyBranchAssignments" ("Id", "PartyId", "BranchId", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('14000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('10000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('00000000-0000-4000-8000-' || lpad((((i - 1) % 5) + 1)::text, 12, '0'))::uuid,
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM generate_series(1, 40) AS i;

        INSERT INTO "PartyRoles" ("Id", "PartyId", "RoleType", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('13000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('10000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            1,
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM generate_series(1, 40) AS i;

        INSERT INTO "PartyRoles" ("Id", "PartyId", "RoleType", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('13000000-0000-4000-8000-' || lpad((40 + i)::text, 12, '0'))::uuid,
            ('10000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            2,
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM generate_series(1, 20) AS i;

        INSERT INTO "Contacts" ("Id", "PartyId", "FirstName", "LastName", "IsPrimary", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('11000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('10000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            'Demo',
            'Contact ' || lpad(i::text, 2, '0'),
            TRUE,
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM generate_series(1, 40) AS i;

        INSERT INTO "Sites" ("Id", "PartyId", "BranchId", "Name", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('12000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('10000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('00000000-0000-4000-8000-' || lpad((((i - 1) % 5) + 1)::text, 12, '0'))::uuid,
            'Fictional Service Site ' || lpad(i::text, 2, '0'),
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM generate_series(1, 40) AS i;
        """;

    private const string SeedOperationalSql =
        """
        INSERT INTO "SalesOpportunities" (
            "Id", "BranchId", "PartyId", "SiteId", "AssignedUserId", "OwnerUserId",
            "Status", "ProposedAmount", "ExpectedCloseDate", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('20000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('00000000-0000-4000-8000-' || lpad((((i - 1) % 5) + 1)::text, 12, '0'))::uuid,
            ('10000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('12000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            '60000000-0000-4000-8000-000000000003',
            '60000000-0000-4000-8000-000000000003',
            ((i - 1) % 8) + 1,
            100000 + (i * 1000),
            DATE '2026-02-01' + i,
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM generate_series(1, 30) AS i;

        INSERT INTO "WorkOrders" (
            "Id", "BranchId", "PartyId", "SiteId", "SalesOpportunityId", "BusinessPartnerId",
            "AssignedUserId", "Status", "ScheduledStartUtc", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('30000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('00000000-0000-4000-8000-' || lpad((((party_number - 1) % 5) + 1)::text, 12, '0'))::uuid,
            ('10000000-0000-4000-8000-' || lpad(party_number::text, 12, '0'))::uuid,
            ('12000000-0000-4000-8000-' || lpad(party_number::text, 12, '0'))::uuid,
            CASE WHEN i IN (6, 14, 22, 30)
                THEN ('20000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid
                ELSE NULL END,
            NULL,
            '60000000-0000-4000-8000-000000000004',
            ((i - 1) % 5) + 1,
            TIMESTAMPTZ '2026-01-02 00:00:00+00' + (i || ' hours')::interval,
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM (
            SELECT i, ((i - 1) % 40) + 1 AS party_number
            FROM generate_series(1, 80) AS i
        ) AS rows;

        INSERT INTO "WorkEvents" (
            "Id", "WorkOrderId", "EventType", "OccurredAtUtc", "BranchId", "Summary",
            "ActorUserId", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('40000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('30000000-0000-4000-8000-' || lpad(work_order_number::text, 12, '0'))::uuid,
            CASE
                WHEN ((work_order_number - 1) % 5) + 1 = 4 AND i <= 80 THEN 3
                ELSE ((i - 1) % 4) + 1
            END,
            TIMESTAMPTZ '2026-01-03 00:00:00+00' + (i || ' minutes')::interval,
            ('00000000-0000-4000-8000-' || lpad((((party_number - 1) % 5) + 1)::text, 12, '0'))::uuid,
            'Fictional work event ' || lpad(i::text, 3, '0'),
            '60000000-0000-4000-8000-000000000004',
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM (
            SELECT
                i,
                ((i - 1) % 80) + 1 AS work_order_number,
                ((((i - 1) % 80)) % 40) + 1 AS party_number
            FROM generate_series(1, 250) AS i
        ) AS rows;

        INSERT INTO "AuditEntries" (
            "Id", "AggregateType", "AggregateId", "BranchId", "Action", "Outcome",
            "ChangeSummary", "OccurredAtUtc", "ActorUserId", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('50000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            'WorkOrder',
            ('30000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('00000000-0000-4000-8000-' || lpad((((i - 1) % 5) + 1)::text, 12, '0'))::uuid,
            'Seeded',
            'Success',
            'Status',
            TIMESTAMPTZ '2026-01-04 00:00:00+00' + (i || ' minutes')::interval,
            '60000000-0000-4000-8000-000000000001',
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM generate_series(1, 20) AS i;
        """;
}