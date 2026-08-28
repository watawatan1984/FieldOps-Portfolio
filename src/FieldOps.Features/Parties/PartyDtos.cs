using System.ComponentModel.DataAnnotations;

using FieldOps.Domain.Enums;

namespace FieldOps.Features.Parties;

public sealed class PartySearchRequest
{
    public Guid BranchId { get; init; }

    [StringLength(100, ErrorMessage = "検索キーワードは100文字以内で入力してください")]
    [Display(Name = "検索キーワード")]
    public string? Search { get; init; }
    public PartyRoleType? Role { get; init; }

    [Range(1, int.MaxValue, ErrorMessage = "ページ番号は1以上で入力してください")]
    [Display(Name = "ページ番号")]
    public int Page { get; init; } = 1;

    [Range(1, int.MaxValue, ErrorMessage = "表示件数は1以上で入力してください")]
    [Display(Name = "表示件数")]
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

    [Required(ErrorMessage = "組織名を入力してください")]
    [StringLength(200, ErrorMessage = "組織名は200文字以内で入力してください")]
    [Display(Name = "組織名")]
    public string OrganizationName { get; set; } = string.Empty;

    [Required(ErrorMessage = "顧客または協力会社を選んでください。")]
    [EnumDataType(typeof(PartyRoleType), ErrorMessage = "顧客または協力会社を選んでください。")]
    [Display(Name = "登録区分")]
    public PartyRoleType? RoleType { get; set; }

    [StringLength(100, ErrorMessage = "担当者の名は100文字以内で入力してください")]
    [Display(Name = "担当者の名")]
    public string? ContactFirstName { get; set; }

    [StringLength(100, ErrorMessage = "担当者の姓は100文字以内で入力してください")]
    [Display(Name = "担当者の姓")]
    public string? ContactLastName { get; set; }

    [StringLength(200, ErrorMessage = "現場名は200文字以内で入力してください")]
    [Display(Name = "現場名")]
    public string? SiteName { get; set; }
}

public sealed class EditPartyInput
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public uint Version { get; set; }

    [Required(ErrorMessage = "組織名を入力してください")]
    [StringLength(200, ErrorMessage = "組織名は200文字以内で入力してください")]
    [Display(Name = "組織名")]
    public string OrganizationName { get; set; } = string.Empty;

    [Display(Name = "顧客")]
    public bool IsCustomer { get; set; }

    [Display(Name = "協力会社")]
    public bool IsBusinessPartner { get; set; }

    public IReadOnlyList<Guid> AssignedBranchIds { get; set; } = [];
}

public sealed class SharePartyInput
{
    public Guid BranchId { get; set; }

    [Required(ErrorMessage = "共有先の支店を選んでください")]
    [Display(Name = "共有先の支店")]
    public Guid? TargetBranchId { get; set; }

    public uint Version { get; set; }
}