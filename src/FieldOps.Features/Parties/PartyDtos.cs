using System.ComponentModel.DataAnnotations;

using FieldOps.Domain.Enums;

namespace FieldOps.Features.Parties;

public sealed class PartySearchRequest
{
    public Guid BranchId { get; init; }

    [StringLength(100)]
    public string? Search { get; init; }
    public PartyRoleType? Role { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = PartyQueries.DefaultPageSize;
}

public sealed record PartyListItem(
    Guid Id,
    string DisplayName,
    bool IsCustomer,
    bool IsBusinessPartner,
    string? PrimaryContact,
    string? PrimarySite,
    uint Version);

public sealed record PartyIndexViewModel(
    Guid BranchId,
    string BranchName,
    string Search,
    PartyRoleType? Role,
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<PartyListItem> Items)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
}

public sealed record PartyDetailsViewModel(
    Guid Id,
    Guid BranchId,
    string BranchName,
    string DisplayName,
    string PartyKind,
    bool IsCustomer,
    bool IsBusinessPartner,
    uint Version,
    IReadOnlyList<string> Contacts,
    IReadOnlyList<string> Sites,
    IReadOnlyList<string> AssignedBranches);

public sealed record BranchOption(Guid Id, string Name);

public sealed class CreatePartyInput
{
    public Guid BranchId { get; set; }

    [Required, StringLength(200)]
    [Display(Name = "Organization name")]
    public string OrganizationName { get; set; } = string.Empty;

    [Required]
    [EnumDataType(typeof(PartyRoleType))]
    [Display(Name = "Initial role")]
    public PartyRoleType RoleType { get; set; } = PartyRoleType.Customer;

    [StringLength(100)]
    [Display(Name = "Contact first name")]
    public string? ContactFirstName { get; set; }

    [StringLength(100)]
    [Display(Name = "Contact last name")]
    public string? ContactLastName { get; set; }

    [StringLength(200)]
    [Display(Name = "Site name")]
    public string? SiteName { get; set; }
}

public sealed class EditPartyInput
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public uint Version { get; set; }

    [Required, StringLength(200)]
    [Display(Name = "Organization name")]
    public string OrganizationName { get; set; } = string.Empty;

    [Display(Name = "Customer")]
    public bool IsCustomer { get; set; }

    [Display(Name = "Business partner")]
    public bool IsBusinessPartner { get; set; }

    public IReadOnlyList<Guid> AssignedBranchIds { get; set; } = [];
}

public sealed class SharePartyInput
{
    public Guid BranchId { get; set; }

    [Required(ErrorMessage = "Select a target branch.")]
    [Display(Name = "Target branch")]
    public Guid? TargetBranchId { get; set; }

    public uint Version { get; set; }
}