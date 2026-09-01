using System.ComponentModel.DataAnnotations;

using FieldOps.Domain.Enums;
using FieldOps.Features.Abstractions;

namespace FieldOps.Features.Quotes;

public sealed class QuoteSearchRequest
{
    public Guid BranchId { get; init; }

    [StringLength(450, ErrorMessage = "営業担当者は450文字以内で指定してください。")]
    [Display(Name = "営業担当者")]
    public string? OwnerUserId { get; init; }

    [EnumDataType(typeof(QuoteStatus), ErrorMessage = "状態の指定が正しくありません。")]
    [Display(Name = "状態")]
    public QuoteStatus? Status { get; init; }

    [Display(Name = "有効期限（開始）")]
    public DateTime? ValidUntilFrom { get; init; }

    [Display(Name = "有効期限（終了）")]
    public DateTime? ValidUntilTo { get; init; }

    [StringLength(100, ErrorMessage = "検索キーワードは100文字以内で入力してください。")]
    [Display(Name = "検索キーワード")]
    public string? Search { get; init; }

    [Range(1, int.MaxValue, ErrorMessage = "ページ番号は1以上で入力してください。")]
    [Display(Name = "ページ番号")]
    public int Page { get; init; } = 1;

    [Range(1, int.MaxValue, ErrorMessage = "表示件数は1以上で入力してください。")]
    [Display(Name = "表示件数")]
    public int PageSize { get; init; } = QuoteQueries.DefaultPageSize;
}

public sealed record QuoteListItem(
    Guid Id,
    string QuoteNumber,
    int RevisionNumber,
    string PartyName,
    string SiteName,
    string BranchName,
    string OwnerName,
    QuoteStatus Status,
    decimal TotalAmount,
    DateTime? ValidUntil,
    uint Version);

public sealed record QuoteIndexViewModel(
    QuoteSearchRequest Filters,
    string BranchName,
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<QuoteListItem> Items,
    IReadOnlyList<FieldOpsUserOption> Owners,
    IReadOnlyList<QuoteBranchOption> Branches,
    bool CanSelectBranch)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
}

public sealed record QuoteBranchOption(Guid Id, string Name);

public sealed record QuoteLineItemView(
    int SortOrder,
    string Description,
    string UnitName,
    decimal Quantity,
    decimal UnitPrice,
    decimal Amount);

public sealed record QuoteAuditSummary(
    DateTime OccurredAtUtc,
    string Action,
    string Outcome,
    string ChangedFields,
    string ActorUserId);

public sealed record QuoteDetailsViewModel(
    Guid Id,
    Guid BranchId,
    Guid SalesOpportunityId,
    string QuoteNumber,
    int RevisionNumber,
    string BranchName,
    string PartyName,
    string SiteName,
    string OwnerName,
    QuoteStatus Status,
    decimal TaxRatePercent,
    decimal Subtotal,
    decimal TaxAmount,
    decimal TotalAmount,
    DateTime? IssuedOn,
    DateTime? ValidUntil,
    string? Notes,
    uint Version,
    bool CanManage,
    bool CanViewAudit,
    IReadOnlyList<QuoteLineItemView> LineItems,
    IReadOnlyList<QuoteStatus> AllowedTransitions,
    IReadOnlyList<QuoteAuditSummary> AuditEntries);

public sealed class QuoteLineItemInput
{
    [Required(ErrorMessage = "品名を入力してください。")]
    [StringLength(200, ErrorMessage = "品名は200文字以内で入力してください。")]
    [Display(Name = "品名")]
    public string Description { get; set; } = string.Empty;

    [Required(ErrorMessage = "単位を入力してください。")]
    [StringLength(16, ErrorMessage = "単位は16文字以内で入力してください。")]
    [Display(Name = "単位")]
    public string UnitName { get; set; } = string.Empty;

    [Range(typeof(decimal), "0.01", "9999999999999999.99", ErrorMessage = "数量は0より大きい値で入力してください。")]
    [Display(Name = "数量")]
    public decimal Quantity { get; set; }

    [Range(typeof(decimal), "0", "9999999999999999.99", ErrorMessage = "単価は0円以上で入力してください。")]
    [Display(Name = "単価")]
    public decimal UnitPrice { get; set; }
}

public sealed class QuoteEditInput
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public uint Version { get; set; }

    [Required(ErrorMessage = "営業案件を選んでください。")]
    [Display(Name = "営業案件")]
    public Guid SalesOpportunityId { get; set; }

    [Required(ErrorMessage = "営業担当者を選んでください。")]
    [Display(Name = "営業担当者")]
    public string OwnerUserId { get; set; } = string.Empty;

    [Range(typeof(decimal), "0", "100", ErrorMessage = "消費税率は0〜100%の範囲で入力してください。")]
    [Display(Name = "消費税率")]
    public decimal TaxRatePercent { get; set; } = 10m;

    [Required(ErrorMessage = "有効期限を入力してください。")]
    [DataType(DataType.Date)]
    [Display(Name = "有効期限")]
    public DateTime? ValidUntil { get; set; }

    [StringLength(2000, ErrorMessage = "備考は2000文字以内で入力してください。")]
    [Display(Name = "備考")]
    public string? Notes { get; set; }

    [MinLength(1, ErrorMessage = "明細を1行以上入力してください。")]
    [Display(Name = "明細")]
    public List<QuoteLineItemInput> LineItems { get; set; } = [];
}

public sealed class QuoteTransitionInput
{
    public uint Version { get; set; }

    [EnumDataType(typeof(QuoteStatus), ErrorMessage = "状態の指定が正しくありません。")]
    [Display(Name = "変更後の状態")]
    public QuoteStatus NextStatus { get; set; }
}

public sealed record QuoteOpportunityOption(
    Guid SalesOpportunityId,
    string PartyName,
    string SiteName,
    SalesOpportunityStatus Status);

public sealed record QuoteEditorOptions(
    string BranchName,
    IReadOnlyList<QuoteOpportunityOption> Opportunities,
    IReadOnlyList<FieldOpsUserOption> Owners);