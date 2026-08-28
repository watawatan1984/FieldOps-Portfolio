using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;
using FieldOps.Features.Abstractions;

using Microsoft.EntityFrameworkCore;

namespace FieldOps.Features.Sales;

public sealed class SalesPageOutOfRangeException : Exception
{
    public SalesPageOutOfRangeException() : base("The page is outside the supported range.") { }
}

public sealed class SalesQueries(
    IFieldOpsDbContext dbContext,
    ICurrentUser currentUser,
    IFieldOpsUserDirectory userDirectory)
{
    private const string SalesRepresentativeRole = "Sales Representative";
    private const string FieldTechnicianRole = "Field Technician";
    private const string SystemAdministratorRole = "System Administrator";
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;

    public async Task<SalesIndexViewModel> SearchAsync(
        SalesSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        int page = Math.Max(1, request.Page);
        int pageSize = request.PageSize <= 0 ? DefaultPageSize : Math.Min(request.PageSize, MaximumPageSize);
        long offset = ((long)page - 1) * pageSize;
        if (offset > int.MaxValue)
        {
            throw new SalesPageOutOfRangeException();
        }

        bool canSelectBranch = currentUser.Role == SystemAdministratorRole;
        if (request.BranchId == Guid.Empty && !canSelectBranch)
        {
            throw new UnauthorizedAccessException("A branch scope is required.");
        }

        IQueryable<SalesOpportunity> opportunities = dbContext.SalesOpportunities.AsNoTracking();
        if (request.BranchId != Guid.Empty)
        {
            opportunities = opportunities.Where(opportunity => opportunity.BranchId == request.BranchId);
        }
        if (currentUser.Role == SalesRepresentativeRole)
        {
            opportunities = opportunities.Where(opportunity => opportunity.OwnerUserId == currentUser.UserId);
        }
        else if (currentUser.Role == FieldTechnicianRole)
        {
            opportunities = opportunities.Where(opportunity => opportunity.AssignedUserId == currentUser.UserId);
        }

        if (!string.IsNullOrWhiteSpace(request.OwnerUserId))
        {
            opportunities = opportunities.Where(opportunity => opportunity.OwnerUserId == request.OwnerUserId);
        }
        if (request.Status is SalesOpportunityStatus status)
        {
            opportunities = opportunities.Where(opportunity => opportunity.Status == status);
        }
        if (request.ExpectedCloseFrom is DateTime expectedFrom)
        {
            opportunities = opportunities.Where(opportunity => opportunity.ExpectedCloseDate >= expectedFrom.Date);
        }
        if (request.ExpectedCloseTo is DateTime expectedTo)
        {
            opportunities = opportunities.Where(opportunity => opportunity.ExpectedCloseDate <= expectedTo.Date);
        }
        if (request.MinimumAmount is decimal minimumAmount)
        {
            opportunities = opportunities.Where(opportunity => opportunity.ProposedAmount >= minimumAmount);
        }
        if (request.MaximumAmount is decimal maximumAmount)
        {
            opportunities = opportunities.Where(opportunity => opportunity.ProposedAmount <= maximumAmount);
        }

        string search = request.Search?.Trim() ?? string.Empty;
        if (search.Length > 0)
        {
            string normalizedSearch = search.ToUpperInvariant();
            opportunities = opportunities.Where(opportunity =>
                dbContext.Parties.Any(party => party.Id == opportunity.PartyId &&
                    ((party.OrganizationName ?? party.LastName + " " + party.FirstName).ToUpper().Contains(normalizedSearch) ||
                     party.Sites.Any(site => site.Id == opportunity.SiteId && site.Name.ToUpper().Contains(normalizedSearch)))));
        }

        int totalCount = await opportunities.CountAsync(cancellationToken);
        List<SalesQueryRow> rows = await opportunities
            .OrderBy(opportunity => opportunity.ExpectedCloseDate == null)
            .ThenBy(opportunity => opportunity.ExpectedCloseDate)
            .ThenBy(opportunity => opportunity.Id)
            .Skip((int)offset)
            .Take(pageSize)
            .Select(opportunity => new SalesQueryRow(
                opportunity.Id,
                dbContext.Parties.Where(party => party.Id == opportunity.PartyId)
                    .Select(party => party.OrganizationName ?? party.LastName + ", " + party.FirstName).Single(),
                dbContext.Parties.SelectMany(party => party.Sites)
                    .Where(site => site.Id == opportunity.SiteId).Select(site => site.Name).Single(),
                dbContext.Branches.Where(branch => branch.Id == opportunity.BranchId)
                    .Select(branch => branch.Name).Single(),
                opportunity.OwnerUserId,
                opportunity.Status,
                opportunity.ProposedAmount,
                opportunity.ExpectedCloseDate,
                opportunity.Version))
            .ToListAsync(cancellationToken);

        Guid? ownerBranchId = request.BranchId == Guid.Empty ? null : request.BranchId;
        IReadOnlyList<FieldOpsUserOption> owners = await userDirectory.GetUsersInRoleAsync(
            ownerBranchId, SalesRepresentativeRole, cancellationToken);
        Dictionary<string, string> ownerNames = owners.ToDictionary(owner => owner.Id, owner => owner.DisplayName);
        List<SalesListItem> items = rows.Select(row => new SalesListItem(
            row.Id,
            row.PartyName,
            row.SiteName,
            row.BranchName,
            row.OwnerUserId is not null && ownerNames.TryGetValue(row.OwnerUserId, out string? ownerName)
                ? ownerName
                : row.OwnerUserId ?? "未割当",
            row.Status,
            row.ProposedAmount,
            row.ExpectedCloseDate,
            row.Version)).ToList();
        string branchName = request.BranchId == Guid.Empty
            ? "全支店"
            : await GetBranchNameAsync(request.BranchId, cancellationToken);
        IReadOnlyList<SalesBranchOption> branches = canSelectBranch
            ? await dbContext.Branches.AsNoTracking()
                .OrderBy(branch => branch.Name)
                .Select(branch => new SalesBranchOption(branch.Id, branch.Name))
                .ToListAsync(cancellationToken)
            : [];
        return new SalesIndexViewModel(
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

    public async Task<SalesEditorOptions> GetEditorOptionsAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        string branchName = await GetBranchNameAsync(branchId, cancellationToken);
        IReadOnlyList<FieldOpsUserOption> owners = await userDirectory.GetUsersInRoleAsync(branchId, SalesRepresentativeRole, cancellationToken);
        IReadOnlyList<FieldOpsUserOption> technicians = await userDirectory.GetUsersInRoleAsync(branchId, FieldTechnicianRole, cancellationToken);
        List<SalesPartySiteOption> partySites = await dbContext.Parties.AsNoTracking()
            .Where(party => party.BranchAssignments.Any(assignment => assignment.BranchId == branchId))
            .SelectMany(party => party.Sites.Where(site => site.BranchId == branchId), (party, site) => new
            {
                PartyId = party.Id,
                PartyName = party.OrganizationName ?? party.LastName + ", " + party.FirstName,
                SiteId = site.Id,
                SiteName = site.Name
            })
            .OrderBy(option => option.PartyName)
            .ThenBy(option => option.SiteName)
            .ThenBy(option => option.SiteId)
            .Select(option => new SalesPartySiteOption(option.PartyId, option.PartyName, option.SiteId, option.SiteName))
            .ToListAsync(cancellationToken);
        return new SalesEditorOptions(branchName, partySites, owners, technicians);
    }

    public Task<SalesEditInput?> GetEditAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.SalesOpportunities.AsNoTracking()
            .Where(opportunity => opportunity.Id == id)
            .Select(opportunity => new SalesEditInput
            {
                Id = opportunity.Id,
                BranchId = opportunity.BranchId,
                Version = opportunity.Version,
                PartyId = opportunity.PartyId,
                SiteId = opportunity.SiteId,
                OwnerUserId = opportunity.OwnerUserId ?? string.Empty,
                AssignedUserId = opportunity.AssignedUserId,
                ProposedAmount = opportunity.ProposedAmount,
                ExpectedCloseDate = opportunity.ExpectedCloseDate
            })
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<SalesDetailsViewModel?> GetDetailsAsync(
        Guid id,
        bool canManage,
        bool canViewAudit,
        CancellationToken cancellationToken = default)
    {
        SalesOpportunity? opportunity = await dbContext.SalesOpportunities.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (opportunity is null)
        {
            return null;
        }

        string branchName = await GetBranchNameAsync(opportunity.BranchId, cancellationToken);
        string partyName = await dbContext.Parties.AsNoTracking().Where(party => party.Id == opportunity.PartyId)
            .Select(party => party.OrganizationName ?? party.LastName + ", " + party.FirstName).SingleAsync(cancellationToken);
        string siteName = await dbContext.Parties.AsNoTracking().SelectMany(party => party.Sites)
            .Where(site => site.Id == opportunity.SiteId).Select(site => site.Name).SingleAsync(cancellationToken);
        IReadOnlyList<FieldOpsUserOption> owners = await userDirectory.GetUsersInRoleAsync(opportunity.BranchId, SalesRepresentativeRole, cancellationToken);
        IReadOnlyList<FieldOpsUserOption> technicians = await userDirectory.GetUsersInRoleAsync(opportunity.BranchId, FieldTechnicianRole, cancellationToken);
        string ownerName = owners.FirstOrDefault(owner => owner.Id == opportunity.OwnerUserId)?.DisplayName
            ?? opportunity.OwnerUserId ?? "未割当";
        string? assignedName = opportunity.AssignedUserId is null
            ? null
            : technicians.FirstOrDefault(user => user.Id == opportunity.AssignedUserId)?.DisplayName ?? opportunity.AssignedUserId;
        IReadOnlyList<SalesAuditSummary> audit = canViewAudit
            ? await dbContext.AuditEntries.AsNoTracking()
                .Where(entry => entry.AggregateType == nameof(SalesOpportunity) && entry.AggregateId == id)
                .OrderBy(entry => entry.OccurredAtUtc)
                .ThenBy(entry => entry.Id)
                .Select(entry => new SalesAuditSummary(entry.OccurredAtUtc, entry.Action, entry.Outcome, entry.ChangeSummary, entry.ActorUserId))
                .ToListAsync(cancellationToken)
            : [];

        return new SalesDetailsViewModel(
            opportunity.Id, opportunity.BranchId, branchName, partyName, siteName, ownerName, assignedName,
            opportunity.Status, opportunity.ProposedAmount, opportunity.ExpectedCloseDate, opportunity.Version,
            canManage, canViewAudit, opportunity.GetAllowedTransitions(), audit);
    }

    private sealed record SalesQueryRow(
        Guid Id,
        string PartyName,
        string SiteName,
        string BranchName,
        string? OwnerUserId,
        SalesOpportunityStatus Status,
        decimal? ProposedAmount,
        DateTime? ExpectedCloseDate,
        uint Version);
}