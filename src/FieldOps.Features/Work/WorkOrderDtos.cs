using System.ComponentModel.DataAnnotations;

using FieldOps.Domain.Enums;
using FieldOps.Features.Abstractions;

namespace FieldOps.Features.Work;

public sealed class WorkOrderSearchRequest
{
    public Guid BranchId { get; init; }

    [EnumDataType(typeof(WorkOrderStatus), ErrorMessage = "状態の指定が正しくありません。")]
    [Display(Name = "状態")]
    public WorkOrderStatus? Status { get; init; }
    public bool Overdue { get; init; }
    public bool Today { get; init; }
    public bool Unassigned { get; init; }
    public bool MissingCompletionRecords { get; init; }

    [Range(1, int.MaxValue, ErrorMessage = "ページ番号は1以上で入力してください。")]
    [Display(Name = "ページ番号")]
    public int Page { get; init; } = 1;

    [Range(1, int.MaxValue, ErrorMessage = "表示件数は1以上で入力してください。")]
    [Display(Name = "表示件数")]
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

    [Required(ErrorMessage = "担当者を選んでください。")]
    [Display(Name = "担当者")]
    public string AssignedUserId { get; set; } = string.Empty;

    [Required(ErrorMessage = "作業開始日時を入力してください。")]
    [Display(Name = "作業開始日時")]
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

    [EnumDataType(typeof(WorkOrderStatus), ErrorMessage = "状態の指定が正しくありません。")]
    [Display(Name = "変更後の状態")]
    public WorkOrderStatus NextStatus { get; set; }
}

public sealed class WorkEventInput
{
    public uint Version { get; set; }

    [EnumDataType(typeof(WorkEventType), ErrorMessage = "記録種別の指定が正しくありません。")]
    [Display(Name = "記録種別")]
    public WorkEventType EventType { get; set; }

    [Required(ErrorMessage = "記録内容を入力してください。")]
    [StringLength(2000, ErrorMessage = "記録内容は2000文字以内で入力してください。")]
    [Display(Name = "記録内容")]
    public string Summary { get; set; } = string.Empty;

    [Required(ErrorMessage = "記録日時を入力してください。")]
    [Display(Name = "記録日時")]
    [DisplayFormat(DataFormatString = "{0:yyyy-MM-ddTHH:mm:ssZ}", ApplyFormatInEditMode = true)]
    public DateTime? OccurredAtUtc { get; set; }
}