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
            "DELETE FROM \"QuoteLineItems\"",
            "DELETE FROM \"AuditEntries\" WHERE \"AggregateType\" <> 'DemoReset'",
            "DELETE FROM \"WorkOrders\"",
            "DELETE FROM \"Quotes\"",
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
                '中央サービス支店',
                '現場サービス支店',
                '北部サービス支店',
                '南部サービス支店',
                '西部サービス支店'])[i],
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM generate_series(1, 5) AS i;

        INSERT INTO "Parties" ("Id", "OrganizationName", "FirstName", "LastName", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('10000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            '架空設備サービス ' || lpad(i::text, 3, '0'),
            NULL,
            NULL,
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM generate_series(1, 250) AS i;

        INSERT INTO "PartyBranchAssignments" ("Id", "PartyId", "BranchId", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('14000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('10000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('00000000-0000-4000-8000-' || lpad((((i - 1) % 5) + 1)::text, 12, '0'))::uuid,
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM generate_series(1, 250) AS i;

        INSERT INTO "PartyRoles" ("Id", "PartyId", "RoleType", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('13000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('10000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            1,
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM generate_series(1, 250) AS i;

        -- Half of all parties (the first 125) also act as business partners, matching the
        -- original 40-party seed's 20/40 (50%) Customer+BusinessPartner overlay ratio.
        INSERT INTO "PartyRoles" ("Id", "PartyId", "RoleType", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('13000000-0000-4000-8000-' || lpad((250 + i)::text, 12, '0'))::uuid,
            ('10000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            2,
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM generate_series(1, 125) AS i;

        INSERT INTO "Contacts" ("Id", "PartyId", "FirstName", "LastName", "IsPrimary", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('11000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('10000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            '担当者' || lpad(i::text, 3, '0'),
            '架空',
            TRUE,
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM generate_series(1, 250) AS i;

        INSERT INTO "Sites" ("Id", "PartyId", "BranchId", "Name", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('12000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('10000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('00000000-0000-4000-8000-' || lpad((((i - 1) % 5) + 1)::text, 12, '0'))::uuid,
            '架空設備 現場 ' || lpad(i::text, 3, '0'),
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM generate_series(1, 250) AS i;
        """;

    private const string SeedOperationalSql =
        """
        -- Status cycles through a 12-slot pattern instead of evenly through all 8 statuses.
        -- 12 is coprime with the 5-branch cycle above, so (unlike a period-10 or period-15
        -- pattern) no branch is ever pinned to the same handful of statuses. The pattern
        -- weights Quoting(4)/Proposed(5) at 6 of 12 slots (50%, versus 25% for a flat 1-in-8
        -- cycle) so that a realistic majority-active pipeline yields enough eligible
        -- opportunities for the ~50-quote-per-branch target below, while New/Contacted/
        -- SurveyScheduled/Won/Lost/OnHold still each appear once per cycle for a believable
        -- spread of pipeline stages.
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
            (ARRAY[1, 2, 3, 4, 5, 4, 5, 6, 7, 8, 4, 5])[((i - 1) % 12) + 1],
            100000 + (i * 1000),
            DATE '2026-02-01' + i,
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM generate_series(1, 250) AS i;

        -- Quotes are generated from every opportunity left in Quoting(4)/Proposed(5) by the
        -- cycle above (~124 of 250), rather than a hand-picked list. Each eligible opportunity
        -- gets 1, 2, or 3 revisions following a repeating [1,2,2,3] pattern (average 2 per
        -- opportunity) so multi-revision quotes stay realistic without every opportunity
        -- carrying the same revision count. BranchId/PartyId/SiteId/OwnerUserId are copied
        -- from the parent opportunity, never computed independently, so they can never drift
        -- from it. QuoteStatus round-robins evenly across all 5 statuses; IssuedOn is NULL
        -- exactly for Draft(1); ValidUntil sits safely before "today" for Expired(5) quotes
        -- and safely after "today" for every other status.
        WITH eligible AS (
            SELECT
                o."Id" AS opportunity_id,
                o."BranchId",
                o."PartyId",
                o."SiteId",
                o."OwnerUserId",
                (ROW_NUMBER() OVER (ORDER BY o."Id"))::int AS eligible_rank
            FROM "SalesOpportunities" o
            WHERE o."Status" IN (4, 5)
        ),
        revisioned AS (
            SELECT
                e.*,
                (ARRAY[1, 2, 2, 3])[((e.eligible_rank - 1) % 4) + 1] AS revision_count
            FROM eligible e
        ),
        expanded AS (
            SELECT
                r."BranchId", r."PartyId", r."SiteId", r.opportunity_id, r."OwnerUserId",
                gs.revision,
                (ROW_NUMBER() OVER (ORDER BY r.eligible_rank, gs.revision))::int AS quote_rank
            FROM revisioned r
            CROSS JOIN LATERAL generate_series(1, r.revision_count) AS gs(revision)
        ),
        statused AS (
            SELECT
                x.*,
                (ARRAY[1, 2, 3, 4, 5])[((x.quote_rank - 1) % 5) + 1] AS status
            FROM expanded x
        )
        INSERT INTO "Quotes" (
            "Id", "BranchId", "PartyId", "SiteId", "SalesOpportunityId", "QuoteNumber", "RevisionNumber",
            "OwnerUserId", "Status", "TaxRatePercent", "Subtotal", "TaxAmount", "TotalAmount",
            "IssuedOn", "ValidUntil", "Notes", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('21000000-0000-4000-8000-' || lpad(quote_rank::text, 12, '0'))::uuid,
            "BranchId",
            "PartyId",
            "SiteId",
            opportunity_id,
            'Q-2026-' || lpad(quote_rank::text, 4, '0'),
            revision,
            "OwnerUserId",
            status,
            10.00,
            0,
            0,
            0,
            CASE WHEN status = 1 THEN NULL ELSE DATE '2026-01-15' + ((quote_rank * 2) % 200) END,
            CASE
                WHEN status = 1 THEN NULL
                WHEN status = 5 THEN DATE '2026-02-01' + (quote_rank % 180)
                ELSE DATE '2026-09-15' + (quote_rank % 365)
            END,
            NULL,
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM statused;

        -- 2-4 line items per quote (count cycles 2,3,4 by quote rank). The item on each line
        -- rotates through a 7-entry catalog of realistic pest-control/field-service work, and
        -- both quantity and unit price vary by quote/position so quotes are not identical.
        -- All quantities and prices are whole numbers (whole units, whole yen).
        WITH numbered_quotes AS (
            SELECT "Id" AS quote_id, (ROW_NUMBER() OVER (ORDER BY "Id"))::int AS quote_rank
            FROM "Quotes"
        ),
        sized AS (
            SELECT quote_id, quote_rank, 2 + (quote_rank % 3) AS line_item_count
            FROM numbered_quotes
        ),
        expanded AS (
            SELECT
                s.quote_id,
                s.quote_rank,
                gs.position,
                ((s.quote_rank + gs.position - 2) % 7) + 1 AS template
            FROM sized s
            CROSS JOIN LATERAL generate_series(1, s.line_item_count) AS gs(position)
        ),
        numbered AS (
            SELECT e.*, (ROW_NUMBER() OVER (ORDER BY e.quote_rank, e.position))::int AS line_item_rank
            FROM expanded e
        )
        INSERT INTO "QuoteLineItems" (
            "Id", "QuoteId", "SortOrder", "Description", "UnitName", "Quantity", "UnitPrice",
            "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('22000000-0000-4000-8000-' || lpad(line_item_rank::text, 12, '0'))::uuid,
            quote_id,
            position,
            (ARRAY['ねずみ防除 初回施工', '捕獲トラップ設置', '定期点検', '薬剤散布', '巡回監視', '報告書作成', '現地調査'])[template],
            (ARRAY['式', '個', '回', '㎡', '時間', '式', '式'])[template],
            CASE template
                WHEN 1 THEN 1
                WHEN 2 THEN 6 + ((quote_rank + position) % 12)
                WHEN 3 THEN 4 + ((quote_rank + position) % 20)
                WHEN 4 THEN 100 + ((quote_rank * 13 + position * 5) % 250)
                WHEN 5 THEN 4 + ((quote_rank + position * 3) % 20)
                WHEN 6 THEN 1
                WHEN 7 THEN 1
            END,
            CASE template
                WHEN 1 THEN 45000 + ((quote_rank * 137) % 20000)
                WHEN 2 THEN 1300 + ((quote_rank * 7) % 300)
                WHEN 3 THEN 6500 + ((quote_rank * 11) % 1500)
                WHEN 4 THEN 300 + ((quote_rank * 3) % 150)
                WHEN 5 THEN 5500 + ((quote_rank * 9) % 1500)
                WHEN 6 THEN 4000 + ((quote_rank * 5) % 2500)
                WHEN 7 THEN 12000 + ((quote_rank * 17) % 8000)
            END,
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM numbered;

        UPDATE "Quotes" AS q
        SET
            "Subtotal" = totals.subtotal,
            "TaxAmount" = FLOOR(totals.subtotal * q."TaxRatePercent" / 100),
            "TotalAmount" = totals.subtotal + FLOOR(totals.subtotal * q."TaxRatePercent" / 100)
        FROM (
            SELECT "QuoteId", SUM("Quantity" * "UnitPrice") AS subtotal
            FROM "QuoteLineItems"
            GROUP BY "QuoteId"
        ) AS totals
        WHERE totals."QuoteId" = q."Id";

        -- Work order i is 1:1 with party/site i (PartyCount now equals WorkOrderCount), which
        -- keeps BranchId trivially consistent with its party and site. Roughly one work order
        -- in twelve (i % 12 = 8) is generated from the identically-numbered sales opportunity
        -- when, and only when, that opportunity landed on Won(6) in the 12-slot status cycle
        -- above (position 8 of the array is the only Won slot) - so the link is always to a
        -- won deal for the same customer/site/branch, without a join back to SalesOpportunities.
        INSERT INTO "WorkOrders" (
            "Id", "BranchId", "PartyId", "SiteId", "SalesOpportunityId", "BusinessPartnerId",
            "AssignedUserId", "Status", "ScheduledStartUtc", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('30000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('00000000-0000-4000-8000-' || lpad((((i - 1) % 5) + 1)::text, 12, '0'))::uuid,
            ('10000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('12000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            CASE WHEN i % 12 = 8
                THEN ('20000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid
                ELSE NULL END,
            NULL,
            '60000000-0000-4000-8000-000000000004',
            ((i - 1) % 5) + 1,
            TIMESTAMPTZ '2026-01-02 00:00:00+00' + (i || ' hours')::interval,
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM generate_series(1, 250) AS i;

        -- 3 events per work order (750 = 250 * 3), round-robining i=1..250 across every work
        -- order for the first event, 251..500 for the second, 501..750 for the third - so
        -- "i <= 250" below identifies each work order's first event, exactly as "i <= 80" did
        -- against the old 80-work-order/250-event ratio. A Completed(4)-status work order's
        -- first event is still forced to Completion(3) so completed work always has a
        -- completion record.
        INSERT INTO "WorkEvents" (
            "Id", "WorkOrderId", "EventType", "OccurredAtUtc", "BranchId", "Summary",
            "ActorUserId", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT
            ('40000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid,
            ('30000000-0000-4000-8000-' || lpad(work_order_number::text, 12, '0'))::uuid,
            CASE
                WHEN ((work_order_number - 1) % 5) + 1 = 4 AND i <= 250 THEN 3
                ELSE ((i - 1) % 4) + 1
            END,
            TIMESTAMPTZ '2026-01-03 00:00:00+00' + (i || ' minutes')::interval,
            ('00000000-0000-4000-8000-' || lpad((((work_order_number - 1) % 5) + 1)::text, 12, '0'))::uuid,
            '架空の作業記録 ' || lpad(i::text, 3, '0'),
            '60000000-0000-4000-8000-000000000004',
            TIMESTAMPTZ '2026-01-01 00:00:00+00',
            TIMESTAMPTZ '2026-01-01 00:00:00+00'
        FROM (
            SELECT
                i,
                ((i - 1) % 250) + 1 AS work_order_number
            FROM generate_series(1, 750) AS i
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