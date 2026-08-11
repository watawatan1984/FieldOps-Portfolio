using FieldOps.Domain.Enums;
using FieldOps.Features.Abstractions;

using Microsoft.EntityFrameworkCore;

namespace FieldOps.Features.Parties;

public sealed class PartyPageOutOfRangeException : Exception
{
    public PartyPageOutOfRangeException() : base("The page is outside the supported range.") { }
}

public sealed class PartyQueries(IFieldOpsDbContext dbContext)
{
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;

    public async Task<PartyIndexViewModel> SearchAsync(
        PartySearchRequest request,
        CancellationToken cancellationToken = default)
    {
        int page = Math.Max(1, request.Page);
        int pageSize = request.PageSize <= 0
            ? DefaultPageSize
            : Math.Min(request.PageSize, MaximumPageSize);
        long offset = ((long)page - 1) * pageSize;
        if (offset > int.MaxValue)
        {
            throw new PartyPageOutOfRangeException();
        }

        string search = request.Search?.Trim() ?? string.Empty;
        string normalizedSearch = search.ToUpperInvariant();

        IQueryable<FieldOps.Domain.Entities.Party> parties = dbContext.Parties
            .AsNoTracking()
            .Where(party => party.BranchAssignments.Any(assignment => assignment.BranchId == request.BranchId));

        if (request.Role is PartyRoleType role)
        {
            parties = parties.Where(party => party.Roles.Any(item => item.RoleType == role));
        }

        if (normalizedSearch.Length > 0)
        {
            parties = parties.Where(party =>
                EF.Property<string>(party, "NormalizedName").Contains(normalizedSearch) ||
                party.Contacts.Any(contact =>
                    (contact.FirstName + " " + contact.LastName).ToUpper().Contains(normalizedSearch) ||
                    (contact.LastName + " " + contact.FirstName).ToUpper().Contains(normalizedSearch)) ||
                party.Sites.Any(site =>
                    site.BranchId == request.BranchId &&
                    site.Name.ToUpper().Contains(normalizedSearch)));
        }

        int totalCount = await parties.CountAsync(cancellationToken);
        List<PartyListItem> items = await parties
            .OrderBy(party => EF.Property<string>(party, "NormalizedName"))
            .ThenBy(party => party.Id)
            .Skip((int)offset)
            .Take(pageSize)
            .Select(party => new PartyListItem(
                party.Id,
                party.OrganizationName ?? party.LastName + ", " + party.FirstName,
                party.Roles.Any(role => role.RoleType == PartyRoleType.Customer),
                party.Roles.Any(role => role.RoleType == PartyRoleType.BusinessPartner),
                party.Contacts
                    .OrderByDescending(contact => contact.IsPrimary)
                    .ThenBy(contact => contact.LastName)
                    .Select(contact => contact.FirstName + " " + contact.LastName)
                    .FirstOrDefault(),
                party.Sites
                    .Where(site => site.BranchId == request.BranchId)
                    .OrderBy(site => site.Name)
                    .Select(site => site.Name)
                    .FirstOrDefault(),
                party.Version))
            .ToListAsync(cancellationToken);

        string branchName = await dbContext.Branches
            .AsNoTracking()
            .Where(branch => branch.Id == request.BranchId)
            .Select(branch => branch.Name)
            .SingleAsync(cancellationToken);

        return new PartyIndexViewModel(
            request.BranchId,
            branchName,
            search,
            request.Role,
            page,
            pageSize,
            totalCount,
            items);
    }

    public Task<string> GetBranchNameAsync(Guid branchId, CancellationToken cancellationToken = default) =>
        dbContext.Branches.AsNoTracking()
            .Where(branch => branch.Id == branchId)
            .Select(branch => branch.Name)
            .SingleAsync(cancellationToken);

    public Task<Guid> GetDefaultBranchIdAsync(CancellationToken cancellationToken = default) =>
        dbContext.Branches.AsNoTracking()
            .OrderBy(branch => branch.Name)
            .Select(branch => branch.Id)
            .FirstAsync(cancellationToken);

    public async Task<PartyDetailsViewModel?> GetDetailsAsync(
        Guid partyId,
        Guid branchId,
        bool includeAllBranchAssignments,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.Parties
            .AsNoTracking()
            .Where(party => party.Id == partyId &&
                party.BranchAssignments.Any(assignment => assignment.BranchId == branchId))
            .Select(party => new PartyDetailsViewModel(
                party.Id,
                branchId,
                dbContext.Branches.Where(branch => branch.Id == branchId).Select(branch => branch.Name).Single(),
                party.OrganizationName ?? party.LastName + ", " + party.FirstName,
                party.IsOrganization ? "Organization" : "Person",
                party.Roles.Any(role => role.RoleType == PartyRoleType.Customer),
                party.Roles.Any(role => role.RoleType == PartyRoleType.BusinessPartner),
                party.Version,
                party.Contacts.OrderByDescending(contact => contact.IsPrimary)
                    .ThenBy(contact => contact.LastName)
                    .Select(contact => contact.FirstName + " " + contact.LastName)
                    .ToList(),
                party.Sites.Where(site => site.BranchId == branchId)
                    .OrderBy(site => site.Name)
                    .Select(site => site.Name)
                    .ToList(),
                party.BranchAssignments
                    .Where(assignment => includeAllBranchAssignments || assignment.BranchId == branchId)
                    .Select(assignment => dbContext.Branches
                        .Where(branch => branch.Id == assignment.BranchId)
                        .Select(branch => branch.Name)
                        .Single())
                    .OrderBy(name => name)
                    .ToList()))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public Task<EditPartyInput?> GetEditAsync(
        Guid partyId,
        Guid branchId,
        CancellationToken cancellationToken = default) =>
        dbContext.Parties.AsNoTracking()
            .Where(party => party.Id == partyId &&
                party.BranchAssignments.Any(assignment => assignment.BranchId == branchId))
            .Select(party => new EditPartyInput
            {
                Id = party.Id,
                BranchId = branchId,
                Version = party.Version,
                OrganizationName = party.OrganizationName ?? string.Empty,
                IsCustomer = party.Roles.Any(role => role.RoleType == PartyRoleType.Customer),
                IsBusinessPartner = party.Roles.Any(role => role.RoleType == PartyRoleType.BusinessPartner),
                AssignedBranchIds = party.BranchAssignments.Select(assignment => assignment.BranchId).ToList()
            })
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<BranchOption>> GetBranchOptionsAsync(
        CancellationToken cancellationToken = default) =>
        await dbContext.Branches.AsNoTracking()
            .OrderBy(branch => branch.Name)
            .Select(branch => new BranchOption(branch.Id, branch.Name))
            .ToListAsync(cancellationToken);
}