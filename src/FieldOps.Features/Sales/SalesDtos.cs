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
    public string? Search { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = SalesQueries.DefaultPageSize;
}

public sealed record SalesListItem(
    Guid Id,
    string PartyName,
    string SiteName,
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
    IReadOnlyList<FieldOpsUserOption> Owners)
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
    IReadOnlyList<SalesOpportunityStatus> AllowedTransitions,
    IReadOnlyList<SalesAuditSummary> AuditEntries);

public sealed class SalesEditInput
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public uint Version { get; set; }

    [Required]
    [Display(Name = "Party")]
    public Guid PartyId { get; set; }

    [Required]
    [Display(Name = "Site")]
    public Guid SiteId { get; set; }

    [Required]
    [Display(Name = "Sales owner")]
    public string OwnerUserId { get; set; } = string.Empty;

    [Display(Name = "Assigned technician")]
    public string? AssignedUserId { get; set; }

    [Range(typeof(decimal), "0.01", "9999999999999999.99")]
    [Display(Name = "Proposed amount")]
    public decimal? ProposedAmount { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "Expected close date")]
    public DateTime? ExpectedCloseDate { get; set; }
}

public sealed class SalesTransitionInput
{
    public uint Version { get; set; }

    [EnumDataType(typeof(SalesOpportunityStatus))]
    public SalesOpportunityStatus NextStatus { get; set; }
}

public sealed record SalesPartySiteOption(Guid PartyId, string PartyName, Guid SiteId, string SiteName);

public sealed record SalesEditorOptions(
    string BranchName,
    IReadOnlyList<SalesPartySiteOption> PartySites,
    IReadOnlyList<FieldOpsUserOption> Owners,
    IReadOnlyList<FieldOpsUserOption> Technicians);