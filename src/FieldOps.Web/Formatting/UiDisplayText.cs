using FieldOps.Domain.Enums;

namespace FieldOps.Web.Formatting;

public static class UiDisplayText
{
    public static string ForRole(string role) => role switch
    {
        "System Administrator" => "システム管理者",
        "Branch Manager" => "支店管理者",
        "Sales Representative" => "営業担当者",
        "Field Technician" => "現場担当者",
        _ => "未定義"
    };

    public static string ForPartyRole(PartyRoleType role) => role switch
    {
        PartyRoleType.Customer => "顧客",
        PartyRoleType.BusinessPartner => "協力会社",
        _ => "未定義"
    };

    public static string ForSalesStatus(SalesOpportunityStatus status) => status switch
    {
        SalesOpportunityStatus.New => "新規",
        SalesOpportunityStatus.Contacted => "連絡済み",
        SalesOpportunityStatus.SurveyScheduled => "現地確認予定",
        SalesOpportunityStatus.Quoting => "見積作成中",
        SalesOpportunityStatus.Proposed => "提案済み",
        SalesOpportunityStatus.Won => "受注",
        SalesOpportunityStatus.Lost => "失注",
        SalesOpportunityStatus.OnHold => "保留",
        _ => "未定義"
    };

    public static string ForSalesStatusDescription(SalesOpportunityStatus status) => status switch
    {
        SalesOpportunityStatus.New => "初回対応待ち",
        SalesOpportunityStatus.Contacted => "要件確認中",
        SalesOpportunityStatus.SurveyScheduled => "現地確認待ち",
        SalesOpportunityStatus.Quoting => "見積準備中",
        SalesOpportunityStatus.Proposed => "回答待ち",
        SalesOpportunityStatus.Won => "作業予定へ引き継ぎ",
        SalesOpportunityStatus.Lost => "対応終了",
        SalesOpportunityStatus.OnHold => "再開待ち",
        _ => "状態を確認してください"
    };

    public static string ForQuoteStatus(QuoteStatus status) => status switch
    {
        QuoteStatus.Draft => "下書き",
        QuoteStatus.Issued => "発行済み",
        QuoteStatus.Accepted => "承認",
        QuoteStatus.Rejected => "失注",
        QuoteStatus.Expired => "期限切れ",
        _ => "未定義"
    };

    public static string ForQuoteStatusDescription(QuoteStatus status) => status switch
    {
        QuoteStatus.Draft => "内容を作成中",
        QuoteStatus.Issued => "顧客の回答待ち",
        QuoteStatus.Accepted => "受注へ進められます",
        QuoteStatus.Rejected => "対応終了",
        QuoteStatus.Expired => "有効期限が過ぎました",
        _ => "状態を確認してください"
    };

    public static string ForWorkOrderStatus(WorkOrderStatus status) => status switch
    {
        WorkOrderStatus.Planned => "未設定",
        WorkOrderStatus.Scheduled => "予定あり",
        WorkOrderStatus.InProgress => "作業中",
        WorkOrderStatus.Completed => "完了",
        WorkOrderStatus.Cancelled => "取り消し",
        _ => "未定義"
    };

    public static string ForWorkEventType(WorkEventType eventType) => eventType switch
    {
        WorkEventType.Note => "記録",
        WorkEventType.Arrival => "到着記録",
        WorkEventType.Completion => "完了記録",
        WorkEventType.Correction => "訂正記録",
        _ => "未定義"
    };

    public static string ForAuditArea(string area) => area switch
    {
        "Party" => "取引先",
        "SalesOpportunity" => "営業案件",
        "Quote" => "見積",
        "WorkOrder" => "作業予定",
        "DemoReset" => "デモリセット",
        _ => "未定義"
    };

    public static string ForAuditAction(string action) => action switch
    {
        "Created" => "作成",
        "Updated" => "更新",
        "ScheduledAndAssigned" => "日程と担当者を設定",
        "StatusChanged" => "状態を変更",
        "WorkEventAdded" => "作業記録を追加",
        "CorrectionAdded" => "訂正記録を追加",
        "ResetStarted" => "リセット開始",
        "ResetCompleted" => "リセット完了",
        "ResetFailed" => "リセット失敗",
        "PostResetMutation" => "リセット後更新",
        "Shared" => "共有",
        _ => "未定義"
    };

    public static string ForAuditOutcome(string outcome)
    {
        string state = outcome.Split(';', 2, StringSplitOptions.TrimEntries)[0];

        return state switch
        {
            "Success" => "成功",
            "Failure" => "失敗",
            "Started" => "開始",
            "Failed" => "失敗",
            "Completed" => "完了",
            "Running" => "実行中",
            _ => "未定義"
        };
    }

    public static string ForAuditFields(string fields)
    {
        string[] displayFields = fields
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ForAuditField)
            .ToArray();

        return displayFields.Length == 0 ? "未定義" : string.Join("、", displayFields);
    }

    private static string ForAuditField(string field) => field switch
    {
        "AssignedUserId" => "担当者",
        "BranchId" => "支店",
        "ContactFirstName" => "担当者名",
        "ContactLastName" => "担当者姓",
        "EventType" => "記録種別",
        "ExpectedCloseDate" => "受注予定日",
        "IsBusinessPartner" => "協力会社区分",
        "IsCustomer" => "顧客区分",
        "LineItems" => "明細",
        "NextStatus" => "変更後の状態",
        "Notes" => "備考",
        "OccurredAtUtc" => "発生日時",
        "OrganizationName" => "組織名",
        "OwnerUserId" => "営業担当者",
        "PartyId" => "取引先",
        "ProposedAmount" => "提案金額",
        "RoleType" => "取引先種別",
        "SalesOpportunityId" => "営業案件",
        "ScheduledStartUtc" => "予定日時",
        "SiteId" => "現場",
        "SiteName" => "現場名",
        "Status" => "状態",
        "Summary" => "内容",
        "TargetBranchId" => "対象支店",
        "TaxRatePercent" => "消費税率",
        "ValidUntil" => "有効期限",
        "詳細は非表示です" => "詳細は非表示です",
        _ => "未定義"
    };
}