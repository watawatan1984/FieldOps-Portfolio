using FieldOps.E2ETests.Infrastructure;
using FieldOps.E2ETests.Pages;
using FieldOps.Infrastructure.Identity;

using Microsoft.Playwright;

namespace FieldOps.E2ETests.Roles;

[Collection(FieldOpsWebCollection.Name)]
public sealed class BranchManagerTests(FieldOpsWebFixture fixture)
{
    [Fact]
    public Task BranchManagerEditsPartySchedulesWorkAndCannotReset() => fixture.RunAsync(
        nameof(BranchManagerEditsPartySchedulesWorkAndCannotReset),
        async (page, errors) =>
        {
            int branchOpenOpportunities = await fixture.QueryScalarAsync<int>(
                "SELECT count(*) FROM \"SalesOpportunities\" WHERE \"BranchId\" = '00000000-0000-4000-8000-000000000001' AND \"Status\" NOT IN (6, 7)");
            int nationalOpenOpportunities = await fixture.QueryScalarAsync<int>(
                "SELECT count(*) FROM \"SalesOpportunities\" WHERE \"Status\" NOT IN (6, 7)");
            Assert.NotEqual(nationalOpenOpportunities, branchOpenOpportunities);
            await fixture.QueryScalarAsync<int>(
                "UPDATE \"AspNetUsers\" SET \"BranchId\" = '00000000-0000-4000-8000-000000000001' WHERE \"Id\" = '60000000-0000-4000-8000-000000000004' RETURNING 1");
            await new DemoLoginPage(page).LoginAsAsync(DemoRoleNames.BranchManager);
            await Assertions.Expect(page.Locator("[data-user-branch='Fictional Central Service Branch']")).ToBeVisibleAsync();
            Assert.Equal(
                branchOpenOpportunities.ToString(System.Globalization.CultureInfo.InvariantCulture),
                await new DashboardPage(page).Metric("open-opportunities").GetAttributeAsync("data-value"));
            string dashboardHtml = await page.ContentAsync();
            Assert.DoesNotContain("Fictional Field Service Branch", dashboardHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("Fictional North Service Branch", dashboardHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("Fictional South Service Branch", dashboardHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("Fictional West Service Branch", dashboardHtml, StringComparison.Ordinal);
            await page.GetByRole(AriaRole.Link, new() { Name = "Customers", Exact = true }).ClickAsync();
            await new PartyPage(page).EditAsync("Fictional Service Customer 01", "Fictional Service Customer 01 Manager");
            await page.GetByRole(AriaRole.Link, new() { Name = "Work orders", Exact = true }).ClickAsync();
            DateTime scheduleStartedUtc = DateTime.UtcNow;
            await new WorkOrderPage(page).ScheduleAsync("Fictional Service Customer 01 Manager");
            ScheduledWorkProof work = await fixture.QuerySingleAsync(
                "SELECT \"Status\", \"AssignedUserId\", \"ScheduledStartUtc\", \"BranchId\" FROM \"WorkOrders\" WHERE \"Id\" = '30000000-0000-4000-8000-000000000001'",
                reader => new ScheduledWorkProof(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetDateTime(2),
                    reader.GetGuid(3)));
            Assert.Equal(2, work.Status);
            Assert.Equal("60000000-0000-4000-8000-000000000004", work.AssignedUserId);
            Assert.Equal(new DateTime(2026, 12, 10, 1, 30, 0, DateTimeKind.Utc), work.ScheduledStartUtc);
            Assert.Equal(DateTimeKind.Utc, work.ScheduledStartUtc.Kind);
            Assert.Equal(Guid.Parse("00000000-0000-4000-8000-000000000001"), work.BranchId);

            ScheduleAuditProof audit = await fixture.QuerySingleAsync(
                "SELECT \"ActorUserId\", \"BranchId\", \"Outcome\", \"ChangeSummary\", \"OccurredAtUtc\" FROM \"AuditEntries\" WHERE \"AggregateId\" = '30000000-0000-4000-8000-000000000001' AND \"Action\" = 'ScheduledAndAssigned'",
                reader => new ScheduleAuditProof(
                    reader.GetString(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetDateTime(4)));
            DateTime scheduleVerifiedUtc = DateTime.UtcNow;
            Assert.Equal("60000000-0000-4000-8000-000000000002", audit.ActorUserId);
            Assert.Equal(Guid.Parse("00000000-0000-4000-8000-000000000001"), audit.BranchId);
            Assert.Equal("Success", audit.Outcome);
            Assert.Equal("AssignedUserId,ScheduledStartUtc,Status", audit.ChangeSummary);
            Assert.Equal(DateTimeKind.Utc, audit.OccurredAtUtc.Kind);
            Assert.InRange(audit.OccurredAtUtc, scheduleStartedUtc.AddSeconds(-1), scheduleVerifiedUtc.AddSeconds(1));
            errors.ExpectForbiddenNavigation("/administration/reset");
            Assert.Equal(403, (await page.GotoAsync("/administration/reset"))!.Status);
        });

    private sealed record ScheduledWorkProof(
        int Status,
        string AssignedUserId,
        DateTime ScheduledStartUtc,
        Guid BranchId);

    private sealed record ScheduleAuditProof(
        string ActorUserId,
        Guid BranchId,
        string Outcome,
        string ChangeSummary,
        DateTime OccurredAtUtc);
}