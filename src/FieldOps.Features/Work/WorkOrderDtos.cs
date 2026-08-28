using System.ComponentModel.DataAnnotations;

using FieldOps.Domain.Enums;
using FieldOps.Features.Abstractions;

namespace FieldOps.Features.Work;

public sealed class WorkOrderSearchRequest
{
    public Guid BranchId { get; init; }
    public WorkOrderStatus? Status { get; init; }
    public bool Overdue { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = WorkOrderQueries.DefaultPageSize;
}

public sealed record WorkOrderListItem(
    Guid Id,
    string PartyName,
    string SiteName,
    string BranchName,
    string? AssignedUserName,
    WorkOrderStatus Status,
    DateTime? ScheduledStartUtc);

public sealed record WorkOrderBranchOption(Guid Id, string Name);

public sealed record WorkOrderIndexViewModel(
    WorkOrderSearchRequest Filters,
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<WorkOrderListItem> Items,
    IReadOnlyList<WorkOrderBranchOption> Branches,
    bool CanSelectBranch)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
}

public sealed class WorkOrderEditInput
{
    public Guid Id { get; set; }
    public uint Version { get; set; }
    public WorkOrderStatus Status { get; set; }

    [Required]
    [Display(Name = "Assigned technician")]
    public string AssignedUserId { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Scheduled start (UTC)")]
    [DisplayFormat(DataFormatString = "{0:yyyy-MM-ddTHH:mm:ssZ}", ApplyFormatInEditMode = true)]
    public DateTime? ScheduledStartUtc { get; set; }
}

public sealed record WorkOrderEditorOptions(
    string PartyName,
    string SiteName,
    string BranchName,
    IReadOnlyList<FieldOpsUserOption> Technicians);

public sealed record WorkEventSummary(
    WorkEventType EventType,
    DateTime OccurredAtUtc,
    string Summary);

public sealed record WorkOrderDetailsViewModel(
    Guid Id,
    Guid BranchId,
    string PartyName,
    string SiteName,
    string BranchName,
    string? AssignedUserName,
    WorkOrderStatus Status,
    DateTime? ScheduledStartUtc,
    uint Version,
    bool CanManage,
    bool CanUpdate,
    bool CanCorrect,
    IReadOnlyList<WorkOrderStatus> AllowedTransitions,
    IReadOnlyList<WorkEventSummary> Events);

public sealed class WorkOrderTransitionInput
{
    public uint Version { get; set; }

    [EnumDataType(typeof(WorkOrderStatus))]
    public WorkOrderStatus NextStatus { get; set; }
}

public sealed class WorkEventInput
{
    public uint Version { get; set; }

    [EnumDataType(typeof(WorkEventType))]
    public WorkEventType EventType { get; set; }

    [Required]
    [StringLength(2000)]
    public string Summary { get; set; } = string.Empty;

    [Required]
    [DisplayFormat(DataFormatString = "{0:yyyy-MM-ddTHH:mm:ssZ}", ApplyFormatInEditMode = true)]
    public DateTime? OccurredAtUtc { get; set; }
}
