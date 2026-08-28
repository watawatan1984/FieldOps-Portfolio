using FieldOps.Features.Dashboard;
using FieldOps.Domain.Enums;
using FieldOps.Infrastructure.Identity;
using FieldOps.Web.Formatting;
using FieldOps.Web.Models;

namespace FieldOps.Web.Services;

public sealed class DashboardPageModelFactory
{
    public DashboardPageViewModel Create(DashboardMetrics metrics, string role, Guid? branchId)
    {
        string salesPath = ScopedPath("/sales", branchId);
        string overdueWorkPath = WorkPath(branchId, overdue: true);
        string scheduledWorkPath = WorkPath(branchId, WorkOrderStatus.Scheduled);
        string inProgressWorkPath = WorkPath(branchId, WorkOrderStatus.InProgress);
        string completedWorkPath = WorkPath(branchId, WorkOrderStatus.Completed);
        string branchesPath = branchId.HasValue ? $"/branches/{branchId.Value}" : "/branches";

        return role switch
        {
            DemoRoleNames.SystemAdministrator => new(
                metrics,
                UiDisplayText.ForRole(role),
                [
                    Card("overdue-work", "全体の遅延", "遅れている作業を確認する。", metrics.OverdueWork, overdueWorkPath),
                    Card("proposals-due", "要確認の提案", "期限を過ぎた提案を確認し、次の対応を決めます。", metrics.ProposalsDue, salesPath),
                    Card("branches", "支店状況", "支店ごとの進み具合を確認します。", metrics.OpenOpportunities, branchesPath, false)
                ],
                [
                    Card("audit", "変更履歴", "最近の更新内容と失敗がないか確認します。", metrics.WorkInProgress, "/audit", false),
                    Card("demo-reset", "デモ管理", "必要なときだけ初期化します。", 0, "/administration/reset", false)
                ]),
            DemoRoleNames.BranchManager => new(
                metrics,
                UiDisplayText.ForRole(role),
                [
                    Card("overdue-work", "期限を過ぎた作業", "担当者と日程を確認する。", metrics.OverdueWork, overdueWorkPath),
                    Card("scheduled-work", "未割当と予定確認", "予定済みの作業に担当者漏れがないか確認します。", metrics.ScheduledWork, scheduledWorkPath),
                    Card("proposals-due", "期限が近い提案", "支店内の提案期限を確認します。", metrics.ProposalsDue, salesPath)
                ],
                [
                    Card("work-in-progress", "進行中の作業", "作業中の案件が止まっていないか確認します。", metrics.WorkInProgress, inProgressWorkPath, false),
                    Card("branch-progress", "支店状況", "支店内の集計を確認します。", metrics.OpenOpportunities, branchesPath, false)
                ]),
            DemoRoleNames.SalesRepresentative => new(
                metrics,
                UiDisplayText.ForRole(role),
                [
                    Card("proposals-due", "期限が近い提案", "営業案件を確認する。", metrics.ProposalsDue, salesPath),
                    Card("open-opportunities", "次の連絡", "未完了の営業案件で次の連絡先を確認します。", metrics.OpenOpportunities, salesPath),
                    Card("scheduled-work", "受注後の作業", "作業予定に進んだ案件を確認します。", metrics.ScheduledWork, scheduledWorkPath, false)
                ],
                [
                    Card("work-in-progress", "現場対応中", "作業中の案件から営業側の確認事項を見ます。", metrics.WorkInProgress, inProgressWorkPath, false),
                    Card("completions-this-month", "今月の完了", "完了した作業を振り返ります。", metrics.CompletionsThisMonth, completedWorkPath, false)
                ]),
            DemoRoleNames.FieldTechnician => new(
                metrics,
                UiDisplayText.ForRole(role),
                [
                    Card("scheduled-work", "今日の作業", "作業予定を確認する。", metrics.ScheduledWork, scheduledWorkPath),
                    Card("work-in-progress", "次の訪問", "作業中の予定と次の訪問先を確認します。", metrics.WorkInProgress, inProgressWorkPath),
                    Card("completions-this-month", "未完了記録", "記録漏れがないか作業履歴を確認します。", metrics.CompletionsThisMonth, "/work-history", false)
                ],
                [
                    Card("overdue-work", "期限を過ぎた作業", "遅れている担当作業を確認します。", metrics.OverdueWork, overdueWorkPath),
                    Card("proposals-due", "営業側の確認", "担当作業に関係する営業案件を確認します。", metrics.ProposalsDue, salesPath, false)
                ]),
            _ => new(metrics, UiDisplayText.ForRole(role), [], [])
        };
    }

    private static DashboardActionCard Card(
        string key,
        string title,
        string description,
        int count,
        string targetPath,
        bool requiresAttention = true) =>
        new(key, title, description, count, targetPath, requiresAttention && count > 0);

    private static string ScopedPath(string path, Guid? branchId) =>
        branchId.HasValue ? $"{path}?branchId={branchId.Value}" : path;

    private static string WorkPath(Guid? branchId, WorkOrderStatus? status = null, bool overdue = false)
    {
        List<string> query = [];
        if (branchId.HasValue) query.Add($"branchId={branchId.Value}");
        if (status.HasValue) query.Add($"status={status.Value}");
        if (overdue) query.Add("overdue=true");
        return query.Count == 0 ? "/work-orders" : $"/work-orders?{string.Join("&", query)}";
    }
}
