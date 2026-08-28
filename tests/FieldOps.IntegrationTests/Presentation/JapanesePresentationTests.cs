using FieldOps.Domain.Enums;
using FieldOps.Web.Formatting;

namespace FieldOps.IntegrationTests.Presentation;

public sealed class JapanesePresentationTests
{
    [Fact]
    public void DisplayTextMapsInternalValuesWithoutChangingTheirStoredNames()
    {
        Assert.Equal("支店管理者", UiDisplayText.ForRole("Branch Manager"));
        Assert.Equal("提案済み", UiDisplayText.ForSalesStatus(SalesOpportunityStatus.Proposed));
        Assert.Equal("作業中", UiDisplayText.ForWorkOrderStatus(WorkOrderStatus.InProgress));
        Assert.Equal("完了記録", UiDisplayText.ForWorkEventType(WorkEventType.Completion));
        Assert.Equal("作業予定", UiDisplayText.ForAuditArea("WorkOrder"));
        Assert.Equal("日程と担当者を設定", UiDisplayText.ForAuditAction("ScheduledAndAssigned"));
        Assert.Equal("成功", UiDisplayText.ForAuditOutcome("Success"));
        Assert.Equal("状態、担当者、予定日時", UiDisplayText.ForAuditFields("Status,AssignedUserId,ScheduledStartUtc"));
        Assert.Equal("Proposed", SalesOpportunityStatus.Proposed.ToString());
        Assert.Equal("InProgress", WorkOrderStatus.InProgress.ToString());
    }

    [Fact]
    public void JapanTimeUsesFriendlyDisplayAndRoundTripsLocalInputToUtc()
    {
        DateTime utc = new(2026, 8, 27, 5, 30, 0, DateTimeKind.Utc);

        Assert.Equal("2026年8月27日 14:30", JapanTimeFormatter.FormatUtc(utc));
        Assert.Equal(new DateOnly(2026, 8, 27), JapanTimeFormatter.ToJapanDate(utc));
        Assert.Equal(new TimeOnly(14, 30), JapanTimeFormatter.ToJapanTime(utc));
        Assert.Equal(utc, JapanTimeFormatter.ToUtc(new DateOnly(2026, 8, 27), new TimeOnly(14, 30)));
    }

    [Fact]
    public void JapanTimeRejectsNonUtcDateTimes()
    {
        DateTime local = new(2026, 8, 27, 14, 30, 0, DateTimeKind.Unspecified);

        Assert.Throws<ArgumentException>(() => JapanTimeFormatter.FormatUtc(local));
        Assert.Throws<ArgumentException>(() => JapanTimeFormatter.ToJapanDate(local));
        Assert.Throws<ArgumentException>(() => JapanTimeFormatter.ToJapanTime(local));
    }
}