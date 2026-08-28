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

    [StringLength(450)]
    public string? TechnicianId { get; set; }

    public DateOnly? ScheduledFrom { get; set; }
    public DateOnly? ScheduledTo { get; set; }
    public DateOnly? CompletedFrom { get; set; }
    public DateOnly? CompletedTo { get; set; }

    [StringLength(100)]
    public string? Keyword { get; set; }

    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    [Range(1, int.MaxValue)]
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
