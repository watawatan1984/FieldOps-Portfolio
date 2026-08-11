using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;
using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace FieldOps.IntegrationTests.Persistence;

[Collection(DatabaseCollection.Name)]
public sealed class ModelMappingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task MigrationsApplyToAnEmptyPostgreSqlDatabase()
    {
        await using FieldOpsDbContext context = await CreateMigratedContextAsync();

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Contains(
            await context.Database.GetAppliedMigrationsAsync(),
            migration => migration.EndsWith("_InitialCreate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PartyWithTwoRolesAndTwoBranchAssignmentsRoundTrips()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (FieldOpsDbContext arrangeContext = CreateContext(connectionString))
        {
            await arrangeContext.Database.MigrateAsync();

            Branch eastBranch = Branch.Create("East Harbor Branch");
            Branch westBranch = Branch.Create("West Harbor Branch");
            Party party = Party.CreateOrganization("Fictional Harbor Services");
            party.AddRole(PartyRoleType.Customer);
            party.AddRole(PartyRoleType.BusinessPartner);
            party.AssignToBranch(eastBranch);
            party.AssignToBranch(westBranch);

            arrangeContext.AddRange(eastBranch, westBranch, party);
            await arrangeContext.SaveChangesAsync();
        }

        await using FieldOpsDbContext assertContext = CreateContext(connectionString);
        Party persisted = await assertContext.Parties
            .Include(party => party.Roles)
            .Include(party => party.BranchAssignments)
            .SingleAsync();

        Assert.Equal("Fictional Harbor Services", persisted.OrganizationName);
        Assert.Equal([PartyRoleType.Customer, PartyRoleType.BusinessPartner], persisted.Roles.Select(role => role.RoleType).Order());
        Assert.Equal(2, persisted.BranchAssignments.Count);
    }

    [Fact]
    public async Task VersionChangesAndAStaleWriteFails()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        Guid partyId;
        uint initialVersion;

        await using (FieldOpsDbContext arrangeContext = CreateContext(connectionString))
        {
            await arrangeContext.Database.MigrateAsync();
            Party party = Party.CreateOrganization("Fictional Concurrent Services");
            arrangeContext.Add(party);
            await arrangeContext.SaveChangesAsync();
            partyId = party.Id;
            initialVersion = party.Version;
        }

        await using FieldOpsDbContext firstContext = CreateContext(connectionString);
        await using FieldOpsDbContext staleContext = CreateContext(connectionString);
        Party first = await firstContext.Parties.SingleAsync(party => party.Id == partyId);
        Party stale = await staleContext.Parties.SingleAsync(party => party.Id == partyId);

        first.AddRole(PartyRoleType.Customer);
        await firstContext.SaveChangesAsync();

        Assert.NotEqual(initialVersion, first.Version);

        stale.AddRole(PartyRoleType.BusinessPartner);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleContext.SaveChangesAsync());
    }

    [Fact]
    public async Task HistoricalWorkEventCannotBeDeletedThroughTheContext()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        Guid workOrderId;

        await using (FieldOpsDbContext arrangeContext = CreateContext(connectionString))
        {
            await arrangeContext.Database.MigrateAsync();
            Branch branch = Branch.Create("Fictional Field Branch");
            Party party = Party.CreateOrganization("Fictional Work Services");
            party.AssignToBranch(branch);
            party.AddSite(branch, "Fictional Plant");
            (SalesOpportunity opportunity, WorkOrder workOrder) = TestWorkOrderFactory.CreateFromWon(branch, party, party.Sites.Single());
            workOrder.AddEvent(WorkEventType.Arrival, new DateTime(2026, 8, 11, 1, 30, 0, DateTimeKind.Utc), "Technician arrived", "fictional.tech");

            arrangeContext.AddRange(branch, party, opportunity, workOrder);
            await arrangeContext.SaveChangesAsync();
            workOrderId = workOrder.Id;
        }

        await using FieldOpsDbContext deleteContext = CreateContext(connectionString);
        WorkOrder persisted = await deleteContext.WorkOrders
            .Include(workOrder => workOrder.Events)
            .SingleAsync(workOrder => workOrder.Id == workOrderId);
        deleteContext.Remove(persisted.Events.Single());

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() => deleteContext.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task HistoricalWorkEventCannotBeBulkDeleted()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();

        await using (FieldOpsDbContext arrangeContext = CreateContext(connectionString))
        {
            await arrangeContext.Database.MigrateAsync();
            Branch branch = Branch.Create("Fictional Bulk Delete Branch");
            Party party = Party.CreateOrganization("Fictional Bulk Delete Services");
            party.AssignToBranch(branch);
            party.AddSite(branch, "Fictional Bulk Delete Site");
            (SalesOpportunity opportunity, WorkOrder workOrder) = TestWorkOrderFactory.CreateFromWon(branch, party, party.Sites.Single());
            workOrder.AddEvent(WorkEventType.Note, new DateTime(2026, 8, 11, 2, 0, 0, DateTimeKind.Utc), "Historical note", "fictional.bulk.user");

            arrangeContext.AddRange(branch, party, opportunity, workOrder);
            await arrangeContext.SaveChangesAsync();
        }

        await using FieldOpsDbContext deleteContext = CreateContext(connectionString);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => deleteContext.Set<WorkEvent>().ExecuteDeleteAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    [Fact]
    public async Task HistoricalAuditEntryCannotBeDeletedThroughTheContext()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();

        await using (FieldOpsDbContext arrangeContext = CreateContext(connectionString))
        {
            await arrangeContext.Database.MigrateAsync();
            arrangeContext.AuditEntries.Add(new AuditEntry(
                nameof(Party),
                Guid.NewGuid(),
                "Fictional audit action",
                new DateTime(2026, 8, 11, 2, 15, 0, DateTimeKind.Utc),
                "fictional.audit.user"));
            await arrangeContext.SaveChangesAsync();
        }

        await using FieldOpsDbContext deleteContext = CreateContext(connectionString);
        AuditEntry persisted = await deleteContext.AuditEntries.SingleAsync();
        deleteContext.Remove(persisted);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() => deleteContext.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task HistoricalAuditEntryCannotBeBulkDeleted()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();

        await using (FieldOpsDbContext arrangeContext = CreateContext(connectionString))
        {
            await arrangeContext.Database.MigrateAsync();
            arrangeContext.AuditEntries.Add(new AuditEntry(
                nameof(Party),
                Guid.NewGuid(),
                "Fictional bulk audit action",
                new DateTime(2026, 8, 11, 2, 30, 0, DateTimeKind.Utc),
                "fictional.bulk.audit.user"));
            await arrangeContext.SaveChangesAsync();
        }

        await using FieldOpsDbContext deleteContext = CreateContext(connectionString);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => deleteContext.AuditEntries.ExecuteDeleteAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    [Fact]
    public async Task SessionScopedBooleanCannotAuthorizeHistoricalDeletes()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();

        await using FieldOpsDbContext context = CreateContext(connectionString);
        await context.Database.MigrateAsync();
        context.AuditEntries.Add(new AuditEntry(
            nameof(Party),
            Guid.NewGuid(),
            "Fictional session bypass audit action",
            new DateTime(2026, 8, 11, 2, 40, 0, DateTimeKind.Utc),
            "fictional.session.user"));
        await context.SaveChangesAsync();

        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("SET fieldops.allow_historical_delete = 'on'");

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => context.AuditEntries.ExecuteDeleteAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    [Fact]
    public async Task CurrentTransactionIdTokenAllowsHistoricalDeletesOnlyInsideItsTransaction()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();

        await using (FieldOpsDbContext arrangeContext = CreateContext(connectionString))
        {
            await arrangeContext.Database.MigrateAsync();
            Branch branch = Branch.Create("Fictional Reset Branch");
            Party party = Party.CreateOrganization("Fictional Reset Services");
            party.AssignToBranch(branch);
            party.AddSite(branch, "Fictional Reset Site");
            (SalesOpportunity opportunity, WorkOrder workOrder) = TestWorkOrderFactory.CreateFromWon(branch, party, party.Sites.Single());
            workOrder.AddEvent(WorkEventType.Note, new DateTime(2026, 8, 11, 2, 45, 0, DateTimeKind.Utc), "Resettable demo history", "fictional.reset.user");
            AuditEntry auditEntry = new(
                nameof(WorkOrder),
                workOrder.Id,
                "Fictional reset audit action",
                new DateTime(2026, 8, 11, 2, 45, 0, DateTimeKind.Utc),
                "fictional.reset.user");

            arrangeContext.AddRange(branch, party, opportunity, workOrder, auditEntry);
            await arrangeContext.SaveChangesAsync();
        }

        await using FieldOpsDbContext resetContext = CreateContext(connectionString);
        await using (var transaction = await resetContext.Database.BeginTransactionAsync())
        {
            await resetContext.Database.ExecuteSqlRawAsync(
                "SELECT set_config('fieldops.allow_historical_delete', txid_current()::text, true)");
            Assert.Equal(1, await resetContext.Set<WorkEvent>().ExecuteDeleteAsync());
            Assert.Equal(1, await resetContext.AuditEntries.ExecuteDeleteAsync());
            await transaction.CommitAsync();
        }

        Assert.Empty(await resetContext.Set<WorkEvent>().ToListAsync());
        Assert.Empty(await resetContext.AuditEntries.ToListAsync());

        resetContext.AuditEntries.Add(new AuditEntry(
            nameof(Party),
            Guid.NewGuid(),
            "Fictional post-reset audit action",
            new DateTime(2026, 8, 11, 3, 0, 0, DateTimeKind.Utc),
            "fictional.reset.user"));
        await resetContext.SaveChangesAsync();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => resetContext.AuditEntries.ExecuteDeleteAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    [Fact]
    public async Task RollingBackTheAuthorizedTransactionRemovesDeleteAuthority()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsDbContext context = CreateContext(connectionString);
        await context.Database.MigrateAsync();
        context.AuditEntries.Add(new AuditEntry(
            nameof(Party),
            Guid.NewGuid(),
            "Fictional rollback audit action",
            new DateTime(2026, 8, 11, 3, 15, 0, DateTimeKind.Utc),
            "fictional.rollback.user"));
        await context.SaveChangesAsync();
        await context.Database.OpenConnectionAsync();

        await using (var authorizedTransaction = await context.Database.BeginTransactionAsync())
        {
            await context.Database.ExecuteSqlRawAsync(
                "SELECT set_config('fieldops.allow_historical_delete', txid_current()::text, true)");
            Assert.Equal(1, await context.AuditEntries.ExecuteDeleteAsync());
            await authorizedTransaction.RollbackAsync();
        }

        Assert.Single(await context.AuditEntries.ToListAsync());

        await using var laterTransaction = await context.Database.BeginTransactionAsync();
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => context.AuditEntries.ExecuteDeleteAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        await laterTransaction.RollbackAsync();
    }

    [Fact]
    public async Task ReusedPooledConnectionAndNewTransactionAreRelocked()
    {
        NpgsqlConnectionStringBuilder pooledConnection = new(await postgres.CreateEmptyDatabaseAsync())
        {
            ApplicationName = $"fieldops-history-pool-{Guid.NewGuid():N}",
            MinPoolSize = 1,
            MaxPoolSize = 1,
            NoResetOnClose = true
        };
        int firstBackendProcessId;
        string firstTransactionId;

        await using (FieldOpsDbContext firstContext = CreateContext(pooledConnection.ConnectionString))
        {
            await firstContext.Database.MigrateAsync();
            firstContext.AuditEntries.Add(new AuditEntry(
                nameof(Party),
                Guid.NewGuid(),
                "Fictional pooled audit action",
                new DateTime(2026, 8, 11, 3, 30, 0, DateTimeKind.Utc),
                "fictional.pool.user"));
            await firstContext.SaveChangesAsync();
            await firstContext.Database.OpenConnectionAsync();
            firstBackendProcessId = await GetBackendProcessIdAsync(firstContext);

            await using var firstTransaction = await firstContext.Database.BeginTransactionAsync();
            firstTransactionId = await GetCurrentTransactionIdAsync(firstContext);
            await firstContext.Database.ExecuteSqlRawAsync(
                "SELECT set_config('fieldops.allow_historical_delete', txid_current()::text, false)");
            await firstTransaction.CommitAsync();
            await firstContext.Database.CloseConnectionAsync();
        }

        await using FieldOpsDbContext reusedContext = CreateContext(pooledConnection.ConnectionString);
        await reusedContext.Database.OpenConnectionAsync();
        Assert.Equal(firstBackendProcessId, await GetBackendProcessIdAsync(reusedContext));
        Assert.Equal(firstTransactionId, await GetHistoricalDeleteSettingAsync(reusedContext));

        await using var laterTransaction = await reusedContext.Database.BeginTransactionAsync();
        Assert.NotEqual(firstTransactionId, await GetCurrentTransactionIdAsync(reusedContext));
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => reusedContext.AuditEntries.ExecuteDeleteAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        await laterTransaction.RollbackAsync();
    }

    [Fact]
    public async Task TimestampsRemainUtcAfterRoundTrip()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        Guid workOrderId;
        DateTime occurredAtUtc = new(2026, 8, 11, 2, 45, 0, DateTimeKind.Utc);
        DateTime scheduledStartUtc = new(2026, 8, 20, 0, 30, 0, DateTimeKind.Utc);

        await using (FieldOpsDbContext arrangeContext = CreateContext(connectionString))
        {
            await arrangeContext.Database.MigrateAsync();
            Branch branch = Branch.Create("Fictional UTC Branch");
            Party party = Party.CreateOrganization("Fictional UTC Services");
            party.AssignToBranch(branch);
            party.AddSite(branch, "Fictional UTC Site");
            (SalesOpportunity opportunity, WorkOrder workOrder) = TestWorkOrderFactory.CreateFromWon(branch, party, party.Sites.Single());
            workOrder.Schedule(scheduledStartUtc, occurredAtUtc);
            workOrder.AddEvent(WorkEventType.Note, occurredAtUtc, "UTC evidence", "fictional.utc.user");
            AuditEntry auditEntry = new(
                nameof(WorkOrder),
                workOrder.Id,
                "Fictional UTC audit action",
                occurredAtUtc,
                "fictional.utc.user");

            arrangeContext.AddRange(branch, party, opportunity, workOrder, auditEntry);
            await arrangeContext.SaveChangesAsync();
            workOrderId = workOrder.Id;
        }

        await using FieldOpsDbContext assertContext = CreateContext(connectionString);
        WorkOrder persisted = await assertContext.WorkOrders
            .Include(workOrder => workOrder.Events)
            .SingleAsync(workOrder => workOrder.Id == workOrderId);
        AuditEntry persistedAudit = await assertContext.AuditEntries.SingleAsync();

        Assert.Equal(DateTimeKind.Utc, persisted.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, persisted.UpdatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, persisted.ScheduledStartUtc?.Kind);
        Assert.Equal(scheduledStartUtc, persisted.ScheduledStartUtc);
        Assert.Equal(DateTimeKind.Utc, persisted.Events.Single().OccurredAtUtc.Kind);
        Assert.Equal(occurredAtUtc, persisted.Events.Single().OccurredAtUtc);
        Assert.Equal(DateTimeKind.Utc, persistedAudit.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, persistedAudit.UpdatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, persistedAudit.OccurredAtUtc.Kind);
        Assert.Equal(occurredAtUtc, persistedAudit.OccurredAtUtc);
    }

    private async Task<FieldOpsDbContext> CreateMigratedContextAsync()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        FieldOpsDbContext context = CreateContext(connectionString);
        await context.Database.MigrateAsync();
        return context;
    }

    private static FieldOpsDbContext CreateContext(string connectionString)
    {
        DbContextOptions<FieldOpsDbContext> options = new DbContextOptionsBuilder<FieldOpsDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new FieldOpsDbContext(options);
    }

    private static async Task<int> GetBackendProcessIdAsync(FieldOpsDbContext context)
    {
        NpgsqlConnection connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await using NpgsqlCommand command = new("SELECT pg_backend_pid()", connection);
        object result = await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("PostgreSQL did not return a backend process identifier.");
        return Convert.ToInt32(result);
    }

    private static async Task<string> GetCurrentTransactionIdAsync(FieldOpsDbContext context)
    {
        NpgsqlConnection connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await using NpgsqlCommand command = new("SELECT txid_current()::text", connection);
        return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("PostgreSQL did not return a transaction identifier."));
    }

    private static async Task<string> GetHistoricalDeleteSettingAsync(FieldOpsDbContext context)
    {
        NpgsqlConnection connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await using NpgsqlCommand command = new("SELECT current_setting('fieldops.allow_historical_delete', true)", connection);
        return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("PostgreSQL did not return the historical-delete setting."));
    }
}
