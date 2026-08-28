using System.ComponentModel.DataAnnotations;

using FieldOps.Domain.Enums;
using FieldOps.Features.Abstractions;
using FieldOps.Features.Work;

namespace FieldOps.Web.Models;

public sealed class WorkHistorySearchViewModel : IValidatableObject
{
    public Guid? BranchId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? BusinessPartnerId { get; set; }
    public Guid? SiteId { get; set; }
    public WorkOrderStatus? WorkStatus { get; set; }
    public WorkEventType? EventType { get; set; }

    [StringLength(450, ErrorMessage = "担当者は450文字以内で指定してください。")]
    [Display(Name = "担当者")]
    public string? TechnicianId { get; set; }

    [Display(Name = "予定日（開始）")]
    public DateOnly? ScheduledFrom { get; set; }

    [Display(Name = "予定日（終了）")]
    public DateOnly? ScheduledTo { get; set; }

    [Display(Name = "完了日（開始）")]
    public DateOnly? CompletedFrom { get; set; }

    [Display(Name = "完了日（終了）")]
    public DateOnly? CompletedTo { get; set; }

    [StringLength(100, ErrorMessage = "キーワードは100文字以内で入力してください。")]
    [Display(Name = "キーワード")]
    public string? Keyword { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "ページ番号は1以上で入力してください。")]
    [Display(Name = "ページ番号")]
    public int Page { get; set; } = 1;

    [Range(1, int.MaxValue, ErrorMessage = "表示件数は1以上で入力してください。")]
    [Display(Name = "表示件数")]
    public int PageSize { get; set; } = WorkHistorySearch.DefaultPageSize;

    public int EffectivePageSize { get; private set; } = WorkHistorySearch.DefaultPageSize;
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; } = 1;
    public bool CanSelectBranch { get; private set; }
    public IReadOnlyList<WorkHistoryListItem> Items { get; private set; } = [];
    public IReadOnlyList<WorkHistoryNamedOption> Branches { get; private set; } = [];
    public IReadOnlyList<WorkHistoryNamedOption> Customers { get; private set; } = [];
    public IReadOnlyList<WorkHistoryNamedOption> BusinessPartners { get; private set; } = [];
    public IReadOnlyList<WorkHistoryNamedOption> Sites { get; private set; } = [];
    public IReadOnlyList<FieldOpsUserOption> Technicians { get; private set; } = [];

    public WorkHistorySearchCriteria ToCriteria() => new(
        BranchId,
        CustomerId,
        BusinessPartnerId,
        SiteId,
        WorkStatus,
        EventType,
        string.IsNullOrWhiteSpace(TechnicianId) ? null : TechnicianId,
        ScheduledFrom,
        ScheduledTo,
        CompletedFrom,
        CompletedTo,
        WorkHistorySearch.NormalizeKeyword(Keyword),
        Page,
        PageSize);

    public void Populate(WorkHistorySearchResult result, WorkHistoryFilterOptions options, bool canSelectBranch)
    {
        Page = result.Page;
        EffectivePageSize = result.PageSize;
        TotalCount = result.TotalCount;
        TotalPages = result.TotalPages;
        Items = result.Items;
        Branches = options.Branches;
        Customers = options.Customers;
        BusinessPartners = options.BusinessPartners;
        Sites = options.Sites;
        Technicians = options.Technicians;
        CanSelectBranch = canSelectBranch;
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ScheduledFrom > ScheduledTo)
        {
            yield return new ValidationResult(
                "予定日の開始日は終了日以前にしてください。",
                [nameof(ScheduledFrom), nameof(ScheduledTo)]);
        }
        if (CompletedFrom > CompletedTo)
        {
            yield return new ValidationResult(
                "完了日の開始日は終了日以前にしてください。",
                [nameof(CompletedFrom), nameof(CompletedTo)]);
        }
    }
}