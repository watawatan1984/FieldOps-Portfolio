using FieldOps.Features.Dashboard;

namespace FieldOps.Web.Models;

public sealed record DashboardActionCard(
    string Key,
    string Title,
    string Description,
    int Count,
    string TargetPath,
    bool RequiresAttention);

public sealed record DashboardPageViewModel(
    DashboardMetrics Metrics,
    string RoleLabel,
    IReadOnlyList<DashboardActionCard> Today,
    IReadOnlyList<DashboardActionCard> Review);
