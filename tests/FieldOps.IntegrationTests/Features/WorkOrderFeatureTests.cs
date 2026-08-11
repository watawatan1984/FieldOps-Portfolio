using System.Net;
using System.Text.RegularExpressions;

using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;
using FieldOps.Features.Abstractions;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

namespace FieldOps.IntegrationTests.Features;

[Collection(DatabaseCollection.Name)]
public sealed class WorkOrderFeatureTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ManagerCreatesOneWorkOrderFromAWonOpportunity()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        WorkSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);
        string token = await GetAntiforgeryTokenAsync(client, $"/sales/{seed.WonOpportunityId}");

        using HttpResponseMessage created = await client.PostAsync(
            $"/work-orders/from-opportunity/{seed.WonOpportunityId}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token
            }));

        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.StartsWith("/work-orders/", created.Headers.Location?.OriginalString);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        WorkOrder workOrder = await dbContext.WorkOrders.SingleAsync();
        Assert.Equal(seed.WonOpportunityId, workOrder.SalesOpportunityId);
        Assert.Equal(seed.BranchId, workOrder.BranchId);
        Assert.Equal(seed.PartyId, workOrder.PartyId);
        Assert.Equal(seed.SiteId, workOrder.SiteId);
        Assert.Equal(WorkOrderStatus.Planned, workOrder.Status);
        Assert.Single(await dbContext.AuditEntries.Where(entry =>
            entry.AggregateType == nameof(WorkOrder) && entry.AggregateId == workOrder.Id).ToListAsync());

        string secondToken = await GetAntiforgeryTokenAsync(client, $"/sales/{seed.WonOpportunityId}");
        using HttpResponseMessage duplicate = await client.PostAsync(
            $"/work-orders/from-opportunity/{seed.WonOpportunityId}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = secondToken
            }));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(1, await dbContext.WorkOrders.CountAsync());
    }

    [Fact]
    public async Task NonWonOpportunityAndSalesRepresentativeCannotCreateWorkOrders()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        WorkSeed seed = await SeedAsync(application);
        Guid nonWonId = await CreateNonWonOpportunityAsync(application, seed.BranchId);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);
        string token = await GetAntiforgeryTokenAsync(client, $"/sales/{nonWonId}");
        using HttpResponseMessage nonWon = await client.PostAsync(
            $"/work-orders/from-opportunity/{nonWonId}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.BadRequest, nonWon.StatusCode);

        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        token = await GetAntiforgeryTokenAsync(client, $"/sales/{nonWonId}");
        using HttpResponseMessage salesCreate = await client.PostAsync(
            $"/work-orders/from-opportunity/{seed.WonOpportunityId}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Forbidden, salesCreate.StatusCode);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>().WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task ManagerSchedulesAndAssignsWorkUsingUtcAndBranchTechnicianOptions()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        WorkSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);
        Guid workOrderId = await CreateThroughHttpAsync(client, seed.WonOpportunityId);
        using HttpResponseMessage plannedDetails = await client.GetAsync($"/work-orders/{workOrderId}");
        string plannedHtml = await plannedDetails.Content.ReadAsStringAsync();
        Assert.Contains("Schedule and assign", plannedHtml);
        Assert.DoesNotContain("Move to Scheduled", plannedHtml);
        using HttpResponseMessage editPage = await client.GetAsync($"/work-orders/{workOrderId}/edit");
        string editHtml = await editPage.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, editPage.StatusCode);
        string token = ExtractAntiforgeryToken(editHtml);
        string version = GetInputValue(editHtml, "Version");

        using HttpResponseMessage response = await client.PostAsync(
            $"/work-orders/{workOrderId}/edit",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Id"] = workOrderId.ToString(),
                ["Version"] = version,
                ["AssignedUserId"] = seed.CentralTechnicianUserId,
                ["ScheduledStartUtc"] = "2026-09-20T01:30:00Z",
                ["__RequestVerificationToken"] = token
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        WorkOrder workOrder = await dbContext.WorkOrders.SingleAsync(item => item.Id == workOrderId);
        Assert.Equal(seed.CentralTechnicianUserId, workOrder.AssignedUserId);
        Assert.Equal(new DateTime(2026, 9, 20, 1, 30, 0, DateTimeKind.Utc), workOrder.ScheduledStartUtc);
        Assert.Equal(WorkOrderStatus.Scheduled, workOrder.Status);
        AuditEntry audit = await dbContext.AuditEntries.SingleAsync(entry =>
            entry.AggregateId == workOrderId && entry.Action == "ScheduledAndAssigned");
        Assert.Equal("AssignedUserId,ScheduledStartUtc,Status", audit.ChangeSummary);
    }

    [Fact]
    public async Task AssignedTechnicianProgressesWorkAndCompletionRequiresAnAppendedCompletionEvent()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        WorkSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);
        Guid id = await CreateThroughHttpAsync(client, seed.WonOpportunityId);
        await ScheduleThroughHttpAsync(client, id, seed.CentralTechnicianUserId);
        await LoginAsAsync(client, DemoRoleNames.FieldTechnician);

        (string token, string version, string detailsHtml) = await GetPageFormAsync(client, $"/work-orders/{id}");
        Assert.DoesNotContain(seed.CentralTechnicianUserId, detailsHtml);
        using HttpResponseMessage started = await client.PostAsync(
            $"/work-orders/{id}/transition",
            TransitionForm(version, WorkOrderStatus.InProgress, token));
        Assert.Equal(HttpStatusCode.Redirect, started.StatusCode);

        (token, version, _) = await GetPageFormAsync(client, $"/work-orders/{id}");
        using HttpResponseMessage prematureCompletion = await client.PostAsync(
            $"/work-orders/{id}/transition",
            TransitionForm(version, WorkOrderStatus.Completed, token));
        Assert.Equal(HttpStatusCode.BadRequest, prematureCompletion.StatusCode);

        (token, version, _) = await GetPageFormAsync(client, $"/work-orders/{id}/events/add");
        using HttpResponseMessage eventAdded = await client.PostAsync(
            $"/work-orders/{id}/events/add",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Version"] = version,
                ["EventType"] = WorkEventType.Completion.ToString(),
                ["OccurredAtUtc"] = "2026-08-11T03:15:00Z",
                ["Summary"] = "Fictional service completed and site secured.",
                ["__RequestVerificationToken"] = token
            }));
        Assert.Equal(HttpStatusCode.Redirect, eventAdded.StatusCode);

        (token, version, string readyToCompleteHtml) = await GetPageFormAsync(client, $"/work-orders/{id}");
        Assert.Contains("Move to Completed", readyToCompleteHtml);
        using HttpResponseMessage completed = await client.PostAsync(
            $"/work-orders/{id}/transition",
            TransitionForm(version, WorkOrderStatus.Completed, token));
        Assert.Equal(HttpStatusCode.Redirect, completed.StatusCode);

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        WorkOrder workOrder = await dbContext.WorkOrders.Include(item => item.Events).SingleAsync(item => item.Id == id);
        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);
        WorkEvent completionEvent = Assert.Single(workOrder.Events);
        Assert.Equal(WorkEventType.Completion, completionEvent.EventType);
        Assert.Equal(seed.CentralTechnicianUserId, completionEvent.ActorUserId);
        Assert.Equal(3, await dbContext.AuditEntries.CountAsync(entry =>
            entry.AggregateType == nameof(WorkOrder) && entry.AggregateId == id &&
            entry.Action != "Created" && entry.Action != "ScheduledAndAssigned"));
    }

    [Fact]
    public async Task WorkOrderPagesEnforceManagerSalesAndAssignedTechnicianScopes()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        WorkSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);
        Guid assignedId = await CreateThroughHttpAsync(client, seed.WonOpportunityId);
        await ScheduleThroughHttpAsync(client, assignedId, seed.CentralTechnicianUserId);
        (Guid centralUnassignedId, Guid otherBranchId) = await AddScopedWorkOrdersAsync(application, seed.BranchId);

        using HttpResponseMessage otherBranchDetails = await client.GetAsync($"/work-orders/{otherBranchId}");
        Assert.Equal(HttpStatusCode.Forbidden, otherBranchDetails.StatusCode);

        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        using HttpResponseMessage salesIndex = await client.GetAsync($"/work-orders?branchId={seed.BranchId}");
        string salesHtml = await salesIndex.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, salesIndex.StatusCode);
        Assert.Contains("Fictional Orchard Facilities", salesHtml);
        Assert.Contains("Fictional Central Backlog", salesHtml);
        (string salesToken, string salesVersion, _) = await GetPageFormAsync(client, $"/work-orders/{assignedId}");
        using HttpResponseMessage salesWrite = await client.PostAsync(
            $"/work-orders/{assignedId}/transition",
            TransitionForm(salesVersion, WorkOrderStatus.InProgress, salesToken));
        Assert.Equal(HttpStatusCode.Forbidden, salesWrite.StatusCode);

        await LoginAsAsync(client, DemoRoleNames.FieldTechnician);
        using HttpResponseMessage technicianIndex = await client.GetAsync($"/work-orders?branchId={seed.BranchId}");
        string technicianHtml = await technicianIndex.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, technicianIndex.StatusCode);
        Assert.Contains("Fictional Orchard Facilities", technicianHtml);
        Assert.DoesNotContain("Fictional Central Backlog", technicianHtml);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/work-orders/{assignedId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/work-orders/{centralUnassignedId}")).StatusCode);
    }

    [Fact]
    public async Task CancellationIsTerminalAndCraftedOrMissingAntiforgeryTransitionsAreRejected()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        WorkSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);
        Guid id = await CreateThroughHttpAsync(client, seed.WonOpportunityId);

        (_, string plannedVersion, _) = await GetPageFormAsync(client, $"/work-orders/{id}");
        using HttpResponseMessage missingAntiforgery = await client.PostAsync(
            $"/work-orders/{id}/transition",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Version"] = plannedVersion,
                ["NextStatus"] = WorkOrderStatus.Cancelled.ToString()
            }));
        Assert.Equal(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);

        (string token, plannedVersion, _) = await GetPageFormAsync(client, $"/work-orders/{id}");
        using HttpResponseMessage crafted = await client.PostAsync(
            $"/work-orders/{id}/transition",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Version"] = plannedVersion,
                ["NextStatus"] = "999",
                ["__RequestVerificationToken"] = token
            }));
        Assert.Equal(HttpStatusCode.BadRequest, crafted.StatusCode);

        (token, plannedVersion, _) = await GetPageFormAsync(client, $"/work-orders/{id}");
        using HttpResponseMessage cancelled = await client.PostAsync(
            $"/work-orders/{id}/transition",
            TransitionForm(plannedVersion, WorkOrderStatus.Cancelled, token));
        Assert.Equal(HttpStatusCode.Redirect, cancelled.StatusCode);

        (token, string cancelledVersion, _) = await GetPageFormAsync(client, $"/work-orders/{id}");
        using HttpResponseMessage terminal = await client.PostAsync(
            $"/work-orders/{id}/transition",
            TransitionForm(cancelledVersion, WorkOrderStatus.InProgress, token));
        Assert.Equal(HttpStatusCode.BadRequest, terminal.StatusCode);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Equal(WorkOrderStatus.Cancelled, await dbContext.WorkOrders.Where(item => item.Id == id).Select(item => item.Status).SingleAsync());
        Assert.Single(await dbContext.AuditEntries.Where(entry => entry.AggregateId == id && entry.Action == "StatusChanged").ToListAsync());
    }

    [Fact]
    public async Task AssignmentTamperAndStaleVersionsDoNotMutateOrDoubleAudit()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        WorkSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);
        Guid id = await CreateThroughHttpAsync(client, seed.WonOpportunityId);
        (string token, string version, _) = await GetPageFormAsync(client, $"/work-orders/{id}/edit");

        using HttpResponseMessage mismatchedId = await client.PostAsync(
            $"/work-orders/{id}/edit",
            ScheduleForm(Guid.NewGuid(), version, seed.CentralTechnicianUserId, token));
        Assert.Equal(HttpStatusCode.BadRequest, mismatchedId.StatusCode);

        (token, version, _) = await GetPageFormAsync(client, $"/work-orders/{id}/edit");
        using HttpResponseMessage nonUtc = await client.PostAsync(
            $"/work-orders/{id}/edit",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Id"] = id.ToString(),
                ["Version"] = version,
                ["AssignedUserId"] = seed.CentralTechnicianUserId,
                ["ScheduledStartUtc"] = "2026-09-20T01:30:00",
                ["__RequestVerificationToken"] = token
            }));
        Assert.Equal(HttpStatusCode.BadRequest, nonUtc.StatusCode);

        using HttpResponseMessage tampered = await client.PostAsync(
            $"/work-orders/{id}/edit",
            ScheduleForm(id, version, "foreign-branch-user-id", token));
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);

        (token, version, _) = await GetPageFormAsync(client, $"/work-orders/{id}/edit");
        using HttpResponseMessage first = await client.PostAsync(
            $"/work-orders/{id}/edit",
            ScheduleForm(id, version, seed.CentralTechnicianUserId, token));
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        using HttpResponseMessage stale = await client.PostAsync(
            $"/work-orders/{id}/edit",
            ScheduleForm(id, version, seed.CentralTechnicianUserId, token));
        string staleHtml = await stale.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("no longer planned", staleHtml);
        Assert.Contains(WorkOrderStatus.Scheduled.ToString(), staleHtml);
        Assert.DoesNotContain("Schedule work order", staleHtml);
        using HttpResponseMessage editAfterScheduled = await client.GetAsync($"/work-orders/{id}/edit");
        Assert.Equal(HttpStatusCode.Redirect, editAfterScheduled.StatusCode);
        Assert.Equal($"/work-orders/{id}", editAfterScheduled.Headers.Location?.OriginalString);

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Single(await dbContext.AuditEntries.Where(entry => entry.AggregateId == id && entry.Action == "ScheduledAndAssigned").ToListAsync());
    }

    [Fact]
    public async Task PlannedScheduleConflictReloadsPersistedFieldsAndCurrentVersion()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        WorkSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);
        Guid id = await CreateThroughHttpAsync(client, seed.WonOpportunityId);
        (string token, string staleVersion, _) = await GetPageFormAsync(client, $"/work-orders/{id}/edit");
        await using (AsyncServiceScope raceScope = application.Services.CreateAsyncScope())
        {
            FieldOpsDbContext raceContext = raceScope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
            WorkOrder concurrentlyAssigned = await raceContext.WorkOrders.SingleAsync(item => item.Id == id);
            concurrentlyAssigned.AssignToUser(seed.CentralTechnicianUserId);
            await raceContext.SaveChangesAsync();
        }

        using HttpResponseMessage response = await client.PostAsync(
            $"/work-orders/{id}/edit",
            ScheduleForm(id, staleVersion, "stale-posted-technician", token));
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Review the latest version", html);
        Assert.NotEqual(staleVersion, GetInputValue(html, "Version"));
        Assert.DoesNotContain("stale-posted-technician", html);
        Assert.Empty(GetInputValue(html, "ScheduledStartUtc"));
        Assert.Matches(
            $"<option(?=[^>]*value=\"{Regex.Escape(seed.CentralTechnicianUserId)}\")(?=[^>]*selected)[^>]*>",
            html);
    }

    [Fact]
    public async Task CompletedWorkAcceptsOnlyAdministratorCorrectionAndDatabaseHistoryIsAppendOnly()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        WorkSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.SystemAdministrator);
        Guid id = await CreateThroughHttpAsync(client, seed.WonOpportunityId);
        await ScheduleThroughHttpAsync(client, id, seed.CentralTechnicianUserId);
        await TransitionThroughHttpAsync(client, id, WorkOrderStatus.InProgress);
        await AddEventThroughHttpAsync(client, id, WorkEventType.Completion, "Fictional completion record.");
        await TransitionThroughHttpAsync(client, id, WorkOrderStatus.Completed);

        await LoginAsAsync(client, DemoRoleNames.BranchManager);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/work-orders/{id}/events/add")).StatusCode);
        await LoginAsAsync(client, DemoRoleNames.SystemAdministrator);

        (string token, string version, string addHtml) = await GetPageFormAsync(client, $"/work-orders/{id}/events/add");
        Assert.Contains($"value=\"{WorkEventType.Correction}\"", addHtml);
        Assert.DoesNotContain($"value=\"{WorkEventType.Note}\"", addHtml);
        Assert.DoesNotContain($"value=\"{WorkEventType.Arrival}\"", addHtml);
        Assert.DoesNotContain($"value=\"{WorkEventType.Completion}\"", addHtml);
        using HttpResponseMessage corrected = await client.PostAsync(
            $"/work-orders/{id}/events/add",
            EventForm(version, WorkEventType.Correction, "Fictional correction: completion reference clarified.", token, "2026-08-11T04:15:00Z"));
        Assert.Equal(HttpStatusCode.Redirect, corrected.StatusCode);
        using HttpResponseMessage details = await client.GetAsync($"/work-orders/{id}");
        string detailsHtml = await details.Content.ReadAsStringAsync();
        Assert.True(detailsHtml.IndexOf("Fictional completion record.", StringComparison.Ordinal) <
                    detailsHtml.IndexOf("Fictional correction: completion reference clarified.", StringComparison.Ordinal));

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand update = connection.CreateCommand();
        update.CommandText = "UPDATE \"WorkEvents\" SET \"Summary\" = 'rewritten' WHERE \"WorkOrderId\" = @id";
        update.Parameters.AddWithValue("id", id);
        PostgresException updateFailure = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Equal("42501", updateFailure.SqlState);
        await using NpgsqlCommand delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM \"WorkEvents\" WHERE \"WorkOrderId\" = @id";
        delete.Parameters.AddWithValue("id", id);
        PostgresException deleteFailure = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Equal("42501", deleteFailure.SqlState);

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Equal(2, await dbContext.WorkOrders.Where(item => item.Id == id).SelectMany(item => item.Events).CountAsync());
        Assert.Single(await dbContext.AuditEntries.Where(entry => entry.AggregateId == id && entry.Action == "CorrectionAdded").ToListAsync());
    }

    [Fact]
    public async Task FutureDatedWorkEventIsRejectedUsingInjectedUtcClock()
    {
        DateTimeOffset currentUtc = new(2026, 9, 20, 3, 30, 0, TimeSpan.Zero);
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            services => services.AddSingleton<TimeProvider>(new FixedTimeProvider(currentUtc)));
        using HttpClient client = CreateClient(application);
        WorkSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.SystemAdministrator);
        Guid id = await CreateThroughHttpAsync(client, seed.WonOpportunityId);
        await ScheduleThroughHttpAsync(client, id, seed.CentralTechnicianUserId);
        await TransitionThroughHttpAsync(client, id, WorkOrderStatus.InProgress);
        (string token, string version, _) = await GetPageFormAsync(client, $"/work-orders/{id}/events/add");

        using HttpResponseMessage response = await client.PostAsync(
            $"/work-orders/{id}/events/add",
            EventForm(version, WorkEventType.Completion, "Fictional future completion evidence.", token, "2026-09-20T03:31:00Z"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Empty(await dbContext.WorkOrders.Where(item => item.Id == id).SelectMany(item => item.Events).ToListAsync());
        Assert.Empty(await dbContext.AuditEntries.Where(entry => entry.AggregateId == id && entry.Action == "WorkEventAdded").ToListAsync());
    }

    [Fact]
    public async Task ConcurrentDuplicateCreateHasOneWinnerAndOneConflict()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient firstClient = CreateClient(application);
        using HttpClient secondClient = CreateClient(application);
        WorkSeed seed = await SeedAsync(application);
        await LoginAsAsync(firstClient, DemoRoleNames.SystemAdministrator);
        await LoginAsAsync(secondClient, DemoRoleNames.SystemAdministrator);
        string firstToken = await GetAntiforgeryTokenAsync(firstClient, $"/sales/{seed.WonOpportunityId}");
        string secondToken = await GetAntiforgeryTokenAsync(secondClient, $"/sales/{seed.WonOpportunityId}");

        Task<HttpResponseMessage> firstTask = firstClient.PostAsync(
            $"/work-orders/from-opportunity/{seed.WonOpportunityId}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = firstToken }));
        Task<HttpResponseMessage> secondTask = secondClient.PostAsync(
            $"/work-orders/from-opportunity/{seed.WonOpportunityId}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = secondToken }));
        HttpResponseMessage[] responses = await Task.WhenAll(firstTask, secondTask);
        using (responses[0])
        using (responses[1])
        {
            Assert.Equal(
                [HttpStatusCode.Redirect, HttpStatusCode.Conflict],
                responses.Select(response => response.StatusCode).OrderBy(status => status).ToArray());
        }

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        WorkOrder workOrder = await dbContext.WorkOrders.SingleAsync();
        Assert.Equal(seed.WonOpportunityId, workOrder.SalesOpportunityId);
        Assert.Single(await dbContext.AuditEntries.Where(entry => entry.AggregateId == workOrder.Id && entry.Action == "Created").ToListAsync());
    }

    [Fact]
    public async Task SimultaneousCompletionTransitionsHaveOneWinnerAndOneRetryableConflict()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient firstClient = CreateClient(application);
        using HttpClient secondClient = CreateClient(application);
        WorkSeed seed = await SeedAsync(application);
        await LoginAsAsync(firstClient, DemoRoleNames.SystemAdministrator);
        await LoginAsAsync(secondClient, DemoRoleNames.SystemAdministrator);
        Guid id = await CreateThroughHttpAsync(firstClient, seed.WonOpportunityId);
        await ScheduleThroughHttpAsync(firstClient, id, seed.CentralTechnicianUserId);
        await TransitionThroughHttpAsync(firstClient, id, WorkOrderStatus.InProgress);
        await AddEventThroughHttpAsync(firstClient, id, WorkEventType.Completion, "Fictional completion race evidence.");
        (string firstToken, string firstVersion, _) = await GetPageFormAsync(firstClient, $"/work-orders/{id}");
        (string secondToken, string secondVersion, _) = await GetPageFormAsync(secondClient, $"/work-orders/{id}");
        Assert.Equal(firstVersion, secondVersion);

        Task<HttpResponseMessage> firstTask = firstClient.PostAsync(
            $"/work-orders/{id}/transition",
            TransitionForm(firstVersion, WorkOrderStatus.Completed, firstToken));
        Task<HttpResponseMessage> secondTask = secondClient.PostAsync(
            $"/work-orders/{id}/transition",
            TransitionForm(secondVersion, WorkOrderStatus.Completed, secondToken));
        HttpResponseMessage[] responses = await Task.WhenAll(firstTask, secondTask);
        using (responses[0])
        using (responses[1])
        {
            Assert.Equal(
                [HttpStatusCode.Redirect, HttpStatusCode.Conflict],
                responses.Select(response => response.StatusCode).OrderBy(status => status).ToArray());
            Assert.Contains("latest version", await responses.Single(response => response.StatusCode == HttpStatusCode.Conflict).Content.ReadAsStringAsync());
        }

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Equal(WorkOrderStatus.Completed, await dbContext.WorkOrders.Where(item => item.Id == id).Select(item => item.Status).SingleAsync());
        Assert.Equal(2, await dbContext.AuditEntries.CountAsync(entry => entry.AggregateId == id && entry.Action == "StatusChanged"));
    }

    [Fact]
    public async Task StaleWorkEventSubmissionHasOneWinnerAndOneRetryableConflict()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient firstClient = CreateClient(application);
        using HttpClient secondClient = CreateClient(application);
        WorkSeed seed = await SeedAsync(application);
        await LoginAsAsync(firstClient, DemoRoleNames.SystemAdministrator);
        await LoginAsAsync(secondClient, DemoRoleNames.SystemAdministrator);
        Guid id = await CreateThroughHttpAsync(firstClient, seed.WonOpportunityId);
        await ScheduleThroughHttpAsync(firstClient, id, seed.CentralTechnicianUserId);
        await TransitionThroughHttpAsync(firstClient, id, WorkOrderStatus.InProgress);
        (string firstToken, string firstVersion, _) = await GetPageFormAsync(firstClient, $"/work-orders/{id}/events/add");
        (string secondToken, string secondVersion, _) = await GetPageFormAsync(secondClient, $"/work-orders/{id}/events/add");
        Assert.Equal(firstVersion, secondVersion);

        Task<HttpResponseMessage> firstTask = firstClient.PostAsync(
            $"/work-orders/{id}/events/add",
            EventForm(firstVersion, WorkEventType.Note, "Fictional first concurrent note.", firstToken));
        Task<HttpResponseMessage> secondTask = secondClient.PostAsync(
            $"/work-orders/{id}/events/add",
            EventForm(secondVersion, WorkEventType.Note, "Fictional second concurrent note.", secondToken));
        HttpResponseMessage[] responses = await Task.WhenAll(firstTask, secondTask);
        using (responses[0])
        using (responses[1])
        {
            Assert.Equal(
                [HttpStatusCode.Redirect, HttpStatusCode.Conflict],
                responses.Select(response => response.StatusCode).OrderBy(status => status).ToArray());
            string conflictHtml = await responses.Single(response => response.StatusCode == HttpStatusCode.Conflict).Content.ReadAsStringAsync();
            Assert.Contains("latest version", conflictHtml);
        }

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Single(await dbContext.WorkOrders.Where(item => item.Id == id).SelectMany(item => item.Events).ToListAsync());
        Assert.Single(await dbContext.AuditEntries.Where(entry => entry.AggregateId == id && entry.Action == "WorkEventAdded").ToListAsync());
    }

    [Fact]
    public async Task TechnicianAssignmentChangeAfterControllerAuthorizationReturnsForbiddenWithoutRequestedMutation()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        AssignmentRacePlan plan = new(connectionString);
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            services =>
            {
                services.RemoveAll<IMutationExecutor>();
                services.AddScoped<IMutationExecutor>(provider => new AssignmentChangingMutationExecutor(
                    provider.GetRequiredService<FieldOpsDbContext>(),
                    plan));
            });
        using HttpClient client = CreateClient(application);
        WorkSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);
        Guid id = await CreateThroughHttpAsync(client, seed.WonOpportunityId);
        await ScheduleThroughHttpAsync(client, id, seed.CentralTechnicianUserId);
        await LoginAsAsync(client, DemoRoleNames.FieldTechnician);
        (string token, string version, _) = await GetPageFormAsync(client, $"/work-orders/{id}");
        plan.Arm(id);

        using HttpResponseMessage response = await client.PostAsync(
            $"/work-orders/{id}/transition",
            TransitionForm(version, WorkOrderStatus.InProgress, token));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        WorkOrder current = await dbContext.WorkOrders.SingleAsync(item => item.Id == id);
        Assert.Null(current.AssignedUserId);
        Assert.Equal(WorkOrderStatus.Scheduled, current.Status);
        Assert.Empty(await dbContext.AuditEntries.Where(entry => entry.AggregateId == id && entry.Action == "StatusChanged").ToListAsync());
    }

    private static async Task<WorkSeed> SeedAsync(FieldOpsWebApplicationFactory application)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Branch branch = await dbContext.Branches.SingleAsync(item => item.Name == "Fictional Central Service Branch");
        ApplicationUser sales = await dbContext.Users.SingleAsync(item => item.UserName == "sales.rep@fieldops.demo");
        ApplicationUser centralTechnician = await dbContext.Users.SingleAsync(item => item.UserName == "field.tech@fieldops.demo");
        centralTechnician.BranchId = branch.Id;
        Party party = Party.CreateOrganization("Fictional Orchard Facilities");
        party.AddRole(PartyRoleType.Customer);
        party.AssignToBranch(branch);
        party.AddSite(branch, "Fictional Orchard Annex");
        Site site = party.Sites.Single();
        SalesOpportunity opportunity = SalesOpportunity.Create(branch, party, site);
        opportunity.AssignOwner(sales.Id);
        opportunity.SetProposal(125000m, new DateTime(2026, 9, 1));
        foreach (SalesOpportunityStatus status in new[]
        {
            SalesOpportunityStatus.Contacted,
            SalesOpportunityStatus.SurveyScheduled,
            SalesOpportunityStatus.Quoting,
            SalesOpportunityStatus.Proposed,
            SalesOpportunityStatus.Won
        }) opportunity.MoveTo(status, DateTime.UtcNow);
        dbContext.AddRange(party, opportunity);
        await dbContext.SaveChangesAsync();
        return new WorkSeed(branch.Id, party.Id, site.Id, opportunity.Id, centralTechnician.Id);
    }

    private static async Task<Guid> CreateThroughHttpAsync(HttpClient client, Guid opportunityId)
    {
        string token = await GetAntiforgeryTokenAsync(client, $"/sales/{opportunityId}");
        using HttpResponseMessage response = await client.PostAsync(
            $"/work-orders/from-opportunity/{opportunityId}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token
            }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Guid.Parse(response.Headers.Location!.OriginalString.Split('/')[^1]);
    }

    private static async Task<(Guid CentralUnassignedId, Guid OtherBranchWorkOrderId)> AddScopedWorkOrdersAsync(
        FieldOpsWebApplicationFactory application,
        Guid centralBranchId)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Branch central = await dbContext.Branches.SingleAsync(item => item.Id == centralBranchId);
        Branch other = await dbContext.Branches.SingleAsync(item => item.Id != centralBranchId);
        Party centralParty = Party.CreateOrganization("Fictional Central Backlog");
        centralParty.AddRole(PartyRoleType.Customer);
        centralParty.AssignToBranch(central);
        centralParty.AddSite(central, "Fictional Central Backlog Site");
        (SalesOpportunity centralOpportunity, WorkOrder centralWork) = TestWorkOrderFactory.CreateFromWon(
            central, centralParty, centralParty.Sites.Single());
        Party otherParty = Party.CreateOrganization("Fictional Other Branch Customer");
        otherParty.AddRole(PartyRoleType.Customer);
        otherParty.AssignToBranch(other);
        otherParty.AddSite(other, "Fictional Other Branch Site");
        (SalesOpportunity otherOpportunity, WorkOrder otherWork) = TestWorkOrderFactory.CreateFromWon(
            other, otherParty, otherParty.Sites.Single());
        dbContext.AddRange(centralParty, centralOpportunity, centralWork, otherParty, otherOpportunity, otherWork);
        await dbContext.SaveChangesAsync();
        return (centralWork.Id, otherWork.Id);
    }

    private static async Task<Guid> CreateNonWonOpportunityAsync(
        FieldOpsWebApplicationFactory application,
        Guid branchId)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Branch branch = await dbContext.Branches.SingleAsync(item => item.Id == branchId);
        ApplicationUser sales = await dbContext.Users.SingleAsync(item => item.UserName == "sales.rep@fieldops.demo");
        Party party = Party.CreateOrganization("Fictional Pending Customer");
        party.AddRole(PartyRoleType.Customer);
        party.AssignToBranch(branch);
        party.AddSite(branch, "Fictional Pending Site");
        SalesOpportunity opportunity = SalesOpportunity.Create(branch, party, party.Sites.Single());
        opportunity.AssignOwner(sales.Id);
        dbContext.AddRange(party, opportunity);
        await dbContext.SaveChangesAsync();
        return opportunity.Id;
    }

    private static async Task ScheduleThroughHttpAsync(HttpClient client, Guid id, string technicianUserId)
    {
        (string token, string version, _) = await GetPageFormAsync(client, $"/work-orders/{id}/edit");
        using HttpResponseMessage response = await client.PostAsync(
            $"/work-orders/{id}/edit",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Id"] = id.ToString(),
                ["Version"] = version,
                ["AssignedUserId"] = technicianUserId,
                ["ScheduledStartUtc"] = "2026-09-20T01:30:00Z",
                ["__RequestVerificationToken"] = token
            }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task TransitionThroughHttpAsync(HttpClient client, Guid id, WorkOrderStatus next)
    {
        (string token, string version, _) = await GetPageFormAsync(client, $"/work-orders/{id}");
        using HttpResponseMessage response = await client.PostAsync(
            $"/work-orders/{id}/transition",
            TransitionForm(version, next, token));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task AddEventThroughHttpAsync(HttpClient client, Guid id, WorkEventType type, string summary)
    {
        (string token, string version, _) = await GetPageFormAsync(client, $"/work-orders/{id}/events/add");
        using HttpResponseMessage response = await client.PostAsync(
            $"/work-orders/{id}/events/add",
            EventForm(version, type, summary, token));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<(string Token, string Version, string Html)> GetPageFormAsync(HttpClient client, string path)
    {
        using HttpResponseMessage response = await client.GetAsync(path);
        string html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (ExtractAntiforgeryToken(html), GetInputValue(html, "Version"), html);
    }

    private static FormUrlEncodedContent TransitionForm(string version, WorkOrderStatus next, string token) =>
        new(new Dictionary<string, string>
        {
            ["Version"] = version,
            ["NextStatus"] = next.ToString(),
            ["__RequestVerificationToken"] = token
        });

    private static FormUrlEncodedContent ScheduleForm(Guid id, string version, string technicianUserId, string token) =>
        new(new Dictionary<string, string>
        {
            ["Id"] = id.ToString(),
            ["Version"] = version,
            ["AssignedUserId"] = technicianUserId,
            ["ScheduledStartUtc"] = "2026-09-20T01:30:00Z",
            ["__RequestVerificationToken"] = token
        });

    private static FormUrlEncodedContent EventForm(
        string version,
        WorkEventType type,
        string summary,
        string token,
        string occurredAtUtc = "2026-08-11T03:15:00Z") =>
        new(new Dictionary<string, string>
        {
            ["Version"] = version,
            ["EventType"] = type.ToString(),
            ["OccurredAtUtc"] = occurredAtUtc,
            ["Summary"] = summary,
            ["__RequestVerificationToken"] = token
        });

    private static async Task LoginAsAsync(HttpClient client, string role)
    {
        using HttpResponseMessage page = await client.GetAsync("/demo-login");
        string html = await page.Content.ReadAsStringAsync();
        string token = ExtractAntiforgeryToken(html);
        string roleToken = Regex.Match(
            html,
            $"<h2 class=\"h5\">{Regex.Escape(role)}</h2>.*?name=\"roleToken\" value=\"([^\"]+)\"",
            RegexOptions.Singleline).Groups[1].Value;
        Assert.NotEmpty(roleToken);
        using HttpResponseMessage response = await client.PostAsync(
            "/demo-login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["roleToken"] = roleToken,
                ["__RequestVerificationToken"] = token
            }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static HttpClient CreateClient(FieldOpsWebApplicationFactory application) =>
        application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        using HttpResponseMessage page = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        return ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        string token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return token;
    }

    private static string GetInputValue(string html, string id) =>
        Regex.Match(html, $"<input(?=[^>]*id=\"{Regex.Escape(id)}\")(?=[^>]*value=\"([^\"]*)\")[^>]*>", RegexOptions.IgnoreCase).Groups[1].Value;

    private sealed record WorkSeed(Guid BranchId, Guid PartyId, Guid SiteId, Guid WonOpportunityId, string CentralTechnicianUserId);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class AssignmentRacePlan(string connectionString)
    {
        public string ConnectionString { get; } = connectionString;
        public Guid WorkOrderId { get; private set; }
        public bool IsArmed { get; private set; }

        public void Arm(Guid workOrderId)
        {
            WorkOrderId = workOrderId;
            IsArmed = true;
        }

        public bool Consume(string operation)
        {
            if (!IsArmed || operation != "work-order-transition") return false;
            IsArmed = false;
            return true;
        }
    }

    private sealed class AssignmentChangingMutationExecutor(
        FieldOpsDbContext dbContext,
        AssignmentRacePlan plan) : IMutationExecutor
    {
        public async Task<TResult> ExecuteAsync<TResult>(
            string operation,
            Func<CancellationToken, Task<TResult>> action,
            CancellationToken cancellationToken = default)
        {
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);
            if (plan.Consume(operation))
            {
                await using NpgsqlConnection connection = new(plan.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using NpgsqlCommand command = connection.CreateCommand();
                command.CommandText = "UPDATE \"WorkOrders\" SET \"AssignedUserId\" = NULL WHERE \"Id\" = @id";
                command.Parameters.AddWithValue("id", plan.WorkOrderId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            TResult result = await action(cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
    }
}
