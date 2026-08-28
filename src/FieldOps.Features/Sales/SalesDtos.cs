using System.ComponentModel.DataAnnotations;

using FieldOps.Domain.Enums;
using FieldOps.Features.Abstractions;

namespace FieldOps.Features.Sales;

public sealed class SalesSearchRequest
{
    public Guid BranchId { get; init; }
    public string? OwnerUserId { get; init; }
    public SalesOpportunityStatus? Status { get; init; }
    public DateTime? ExpectedCloseFrom { get; init; }
    public DateTime? ExpectedCloseTo { get; init; }
    public decimal? MinimumAmount { get; init; }
    public decimal? MaximumAmount { get; init; }

    [StringLength(100)]
    public string? Search { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = SalesQueries.DefaultPageSize;
}

public sealed record SalesListItem(
    Guid Id,
    string PartyName,
    string SiteName,
    string BranchName,
    string OwnerName,
    SalesOpportunityStatus Status,
    decimal? ProposedAmount,
    DateTime? ExpectedCloseDate,
    uint Version);

public sealed record SalesIndexViewModel(
    SalesSearchRequest Filters,
    string BranchName,
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<SalesListItem> Items,
    IReadOnlyList<FieldOpsUserOption> Owners,
    IReadOnlyList<SalesBranchOption> Branches,
    bool CanSelectBranch)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
}

public sealed record SalesAuditSummary(
    DateTime OccurredAtUtc,
    string Action,
    string Outcome,
    string ChangedFields,
    string ActorUserId);

public sealed record SalesDetailsViewModel(
    Guid Id,
    Guid BranchId,
    string BranchName,
    string PartyName,
    string SiteName,
    string OwnerName,
    string? AssignedUserName,
    SalesOpportunityStatus Status,
    decimal? ProposedAmount,
    DateTime? ExpectedCloseDate,
    uint Version,
    bool CanManage,
    bool CanViewAudit,
    IReadOnlyList<SalesOpportunityStatus> AllowedTransitions,
    IReadOnlyList<SalesAuditSummary> AuditEntries);

public sealed class SalesEditInput
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public uint Version { get; set; }

    [Required(ErrorMessage = "顧客を選んでください。")]
    [Display(Name = "顧客")]
    public Guid PartyId { get; set; }

    [Required(ErrorMessage = "現場を選んでください。")]
    [Display(Name = "現場")]
    public Guid SiteId { get; set; }

    [Required(ErrorMessage = "営業担当者を選んでください。")]
    [Display(Name = "営業担当者")]
    public string OwnerUserId { get; set; } = string.Empty;

    [Display(Name = "現場担当者")]
    public string? AssignedUserId { get; set; }

    [Range(typeof(decimal), "0.01", "9999999999999999.99", ErrorMessage = "提案金額は1円以上で入力してください。")]
    [Display(Name = "提案金額")]
    public decimal? ProposedAmount { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "予定日")]
    public DateTime? ExpectedCloseDate { get; set; }
}

public sealed class SalesTransitionInput
{
    public uint Version { get; set; }

    [EnumDataType(typeof(SalesOpportunityStatus))]
    public SalesOpportunityStatus NextStatus { get; set; }
}

public sealed record SalesPartySiteOption(Guid PartyId, string PartyName, Guid SiteId, string SiteName);

public sealed record SalesBranchOption(Guid Id, string Name);

public sealed record SalesEditorOptions(
    string BranchName,
    IReadOnlyList<SalesPartySiteOption> PartySites,
    IReadOnlyList<FieldOpsUserOption> Owners,
    IReadOnlyList<FieldOpsUserOption> Technicians);