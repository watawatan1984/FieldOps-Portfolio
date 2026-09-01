using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;
using FieldOps.Features.Abstractions;

using Microsoft.EntityFrameworkCore;

namespace FieldOps.Features.Quotes;

public sealed class QuotePageOutOfRangeException : Exception
{
    public QuotePageOutOfRangeException() : base("The page is outside the supported range.") { }
}

public sealed class QuoteQueries(
    IFieldOpsDbContext dbContext,
    ICurrentUser currentUser,
    IFieldOpsUserDirectory userDirectory)
{
    private const string SalesRepresentativeRole = "Sales Representative";
    private const string SystemAdministratorRole = "System Administrator";
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;

    public async Task<QuoteIndexViewModel> SearchAsync(
        QuoteSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        int page = Math.Max(1, request.Page);
        int pageSize = request.PageSize <= 0 ? DefaultPageSize : Math.Min(request.PageSize, MaximumPageSize);
        long offset = ((long)page - 1) * pageSize;
        if (offset > int.MaxValue)
        {
            throw new QuotePageOutOfRangeException();
        }

        bool canSelectBranch = currentUser.Role == SystemAdministratorRole;
        if (request.BranchId == Guid.Empty && !canSelectBranch)
        {
            throw new UnauthorizedAccessException("A branch scope is required.");
        }

        IQueryable<Quote> quotes = dbContext.Quotes.AsNoTracking();
        if (request.BranchId != Guid.Empty)
        {
            quotes = quotes.Where(quote => quote.BranchId == request.BranchId);
        }
        if (currentUser.Role == SalesRepresentativeRole)
        {
            quotes = quotes.Where(quote => quote.OwnerUserId == currentUser.UserId);
        }

        if (!string.IsNullOrWhiteSpace(request.OwnerUserId))
        {
            quotes = quotes.Where(quote => quote.OwnerUserId == request.OwnerUserId);
        }
        if (request.Status is QuoteStatus status)
        {
            quotes = quotes.Where(quote => quote.Status == status);
        }
        if (request.ValidUntilFrom is DateTime validFrom)
        {
            quotes = quotes.Where(quote => quote.ValidUntil >= validFrom.Date);
        }
        if (request.ValidUntilTo is DateTime validTo)
        {
            quotes = quotes.Where(quote => quote.ValidUntil <= validTo.Date);
        }

        string search = request.Search?.Trim() ?? string.Empty;
        if (search.Length > 0)
        {
            string normalizedSearch = search.ToUpperInvariant();
            quotes = quotes.Where(quote =>
                quote.QuoteNumber.ToUpper().Contains(normalizedSearch) ||
                dbContext.Parties.Any(party => party.Id == quote.PartyId &&
                    ((party.OrganizationName ?? party.LastName + " " + party.FirstName).ToUpper().Contains(normalizedSearch) ||
                     party.Sites.Any(site => site.Id == quote.SiteId && site.Name.ToUpper().Contains(normalizedSearch)))));
        }

        int totalCount = await quotes.CountAsync(cancellationToken);
        List<QuoteQueryRow> rows = await quotes
            .OrderBy(quote => quote.ValidUntil == null)
            .ThenBy(quote => quote.ValidUntil)
            .ThenBy(quote => quote.Id)
            .Skip((int)offset)
            .Take(pageSize)
            .Select(quote => new QuoteQueryRow(
                quote.Id,
                quote.QuoteNumber,
                quote.RevisionNumber,
                dbContext.Parties.Where(party => party.Id == quote.PartyId)
                    .Select(party => party.OrganizationName ?? party.LastName + ", " + party.FirstName).Single(),
                dbContext.Parties.SelectMany(party => party.Sites)
                    .Where(site => site.Id == quote.SiteId).Select(site => site.Name).Single(),
                dbContext.Branches.Where(branch => branch.Id == quote.BranchId)
                    .Select(branch => branch.Name).Single(),
                quote.OwnerUserId,
                quote.Status,
                quote.TotalAmount,
                quote.ValidUntil,
                quote.Version))
            .ToListAsync(cancellationToken);

        Guid? ownerBranchId = request.BranchId == Guid.Empty ? null : request.BranchId;
        IReadOnlyList<FieldOpsUserOption> owners = await userDirectory.GetUsersInRoleAsync(
            ownerBranchId, SalesRepresentativeRole, cancellationToken);
        IReadOnlyDictionary<string, string> ownerDisplayNames = await userDirectory.GetDisplayNamesAsync(
            rows.Select(row => row.OwnerUserId).OfType<string>().Distinct(StringComparer.Ordinal),
            cancellationToken);
        List<QuoteListItem> items = rows.Select(row => new QuoteListItem(
            row.Id,
            row.QuoteNumber,
            row.RevisionNumber,
            row.PartyName,
            row.SiteName,
            row.BranchName,
            row.OwnerUserId is not null && ownerDisplayNames.TryGetValue(row.OwnerUserId, out string? ownerName)
                ? ownerName
                : "未割当",
            row.Status,
            row.TotalAmount,
            row.ValidUntil,
            row.Version)).ToList();
        string branchName = request.BranchId == Guid.Empty
            ? "全支店"
            : await GetBranchNameAsync(request.BranchId, cancellationToken);
        IReadOnlyList<QuoteBranchOption> branches = canSelectBranch
            ? await dbContext.Branches.AsNoTracking()
                .OrderBy(branch => branch.Name)
                .Select(branch => new QuoteBranchOption(branch.Id, branch.Name))
                .ToListAsync(cancellationToken)
            : [];
        return new QuoteIndexViewModel(
            request,
            branchName,
            page,
            pageSize,
            totalCount,
            items,
            owners,
            branches,
            canSelectBranch);
    }

    public Task<Guid> GetDefaultBranchIdAsync(CancellationToken cancellationToken = default) =>
        dbContext.Branches.AsNoTracking().OrderBy(branch => branch.Name).Select(branch => branch.Id).FirstAsync(cancellationToken);

    public Task<string> GetBranchNameAsync(Guid branchId, CancellationToken cancellationToken = default) =>
        dbContext.Branches.AsNoTracking().Where(branch => branch.Id == branchId).Select(branch => branch.Name).SingleAsync(cancellationToken);

    public async Task<QuoteEditorOptions> GetEditorOptionsAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        string branchName = await GetBranchNameAsync(branchId, cancellationToken);
        IReadOnlyList<FieldOpsUserOption> owners = await userDirectory.GetUsersInRoleAsync(branchId, SalesRepresentativeRole, cancellationToken);

        IQueryable<SalesOpportunity> opportunities = dbContext.SalesOpportunities.AsNoTracking()
            .Where(opportunity => opportunity.BranchId == branchId)
            .Where(opportunity => opportunity.Status == SalesOpportunityStatus.Quoting ||
                                  opportunity.Status == SalesOpportunityStatus.Proposed);
        if (currentUser.Role == SalesRepresentativeRole)
        {
            opportunities = opportunities.Where(opportunity => opportunity.OwnerUserId == currentUser.UserId);
        }

        List<QuoteOpportunityOption> options = await opportunities
            .Select(opportunity => new
            {
                opportunity.Id,
                PartyName = dbContext.Parties.Where(party => party.Id == opportunity.PartyId)
                    .Select(party => party.OrganizationName ?? party.LastName + ", " + party.FirstName).Single(),
                SiteName = dbContext.Parties.SelectMany(party => party.Sites)
                    .Where(site => site.Id == opportunity.SiteId).Select(site => site.Name).Single(),
                opportunity.Status
            })
            .OrderBy(option => option.PartyName)
            .ThenBy(option => option.SiteName)
            .ThenBy(option => option.Id)
            .Select(option => new QuoteOpportunityOption(option.Id, option.PartyName, option.SiteName, option.Status))
            .ToListAsync(cancellationToken);

        return new QuoteEditorOptions(branchName, options, owners);
    }

    public async Task<QuoteEditInput?> GetEditAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Quote? quote = await dbContext.Quotes.AsNoTracking()
            .Include(item => item.LineItems)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (quote is null)
        {
            return null;
        }

        return new QuoteEditInput
        {
            Id = quote.Id,
            BranchId = quote.BranchId,
            Version = quote.Version,
            SalesOpportunityId = quote.SalesOpportunityId,
            OwnerUserId = quote.OwnerUserId ?? string.Empty,
            TaxRatePercent = quote.TaxRatePercent,
            ValidUntil = quote.ValidUntil,
            Notes = quote.Notes,
            LineItems = [.. quote.LineItems
                .OrderBy(lineItem => lineItem.SortOrder)
                .Select(lineItem => new QuoteLineItemInput
                {
                    Description = lineItem.Description,
                    UnitName = lineItem.UnitName,
                    Quantity = lineItem.Quantity,
                    UnitPrice = lineItem.UnitPrice
                })]
        };
    }

    public async Task<QuoteDetailsViewModel?> GetDetailsAsync(
        Guid id,
        bool canManage,
        bool canViewAudit,
        CancellationToken cancellationToken = default)
    {
        Quote? quote = await dbContext.Quotes.AsNoTracking()
            .Include(item => item.LineItems)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (quote is null)
        {
            return null;
        }

        string branchName = await GetBranchNameAsync(quote.BranchId, cancellationToken);
        string partyName = await dbContext.Parties.AsNoTracking().Where(party => party.Id == quote.PartyId)
            .Select(party => party.OrganizationName ?? party.LastName + ", " + party.FirstName).SingleAsync(cancellationToken);
        string siteName = await dbContext.Parties.AsNoTracking().SelectMany(party => party.Sites)
            .Where(site => site.Id == quote.SiteId).Select(site => site.Name).SingleAsync(cancellationToken);
        List<string> involvedUserIds = [];
        if (quote.OwnerUserId is not null) involvedUserIds.Add(quote.OwnerUserId);
        IReadOnlyDictionary<string, string> userDisplayNames = await userDirectory.GetDisplayNamesAsync(involvedUserIds, cancellationToken);
        string ownerName = quote.OwnerUserId is not null && userDisplayNames.TryGetValue(quote.OwnerUserId, out string? resolvedOwnerName)
            ? resolvedOwnerName
            : "未割当";
        IReadOnlyList<QuoteAuditSummary> audit = canViewAudit
            ? await dbContext.AuditEntries.AsNoTracking()
                .Where(entry => entry.AggregateType == nameof(Quote) && entry.AggregateId == id)
                .OrderBy(entry => entry.OccurredAtUtc)
                .ThenBy(entry => entry.Id)
                .Select(entry => new QuoteAuditSummary(entry.OccurredAtUtc, entry.Action, entry.Outcome, entry.ChangeSummary, entry.ActorUserId))
                .ToListAsync(cancellationToken)
            : [];

        List<QuoteLineItemView> lineItems = [.. quote.LineItems
            .OrderBy(lineItem => lineItem.SortOrder)
            .Select(lineItem => new QuoteLineItemView(
                lineItem.SortOrder,
                lineItem.Description,
                lineItem.UnitName,
                lineItem.Quantity,
                lineItem.UnitPrice,
                lineItem.Amount))];

        return new QuoteDetailsViewModel(
            quote.Id, quote.BranchId, quote.SalesOpportunityId, quote.QuoteNumber, quote.RevisionNumber,
            branchName, partyName, siteName, ownerName, quote.Status,
            quote.TaxRatePercent, quote.Subtotal, quote.TaxAmount, quote.TotalAmount,
            quote.IssuedOn, quote.ValidUntil, quote.Notes, quote.Version,
            canManage, canViewAudit, lineItems, quote.GetAllowedTransitions(), audit);
    }

    private sealed record QuoteQueryRow(
        Guid Id,
        string QuoteNumber,
        int RevisionNumber,
        string PartyName,
        string SiteName,
        string BranchName,
        string? OwnerUserId,
        QuoteStatus Status,
        decimal TotalAmount,
        DateTime? ValidUntil,
        uint Version);
}