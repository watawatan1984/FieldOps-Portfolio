using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;
using FieldOps.Features.Abstractions;

using Microsoft.EntityFrameworkCore;

namespace FieldOps.Features.Parties;

public sealed class PartyDuplicateException : Exception
{
    public PartyDuplicateException() : base("A party with this normalized name already exists.") { }
}

public sealed class PartyConcurrencyException : Exception
{
    public PartyConcurrencyException() : base("The party was changed by another user.") { }
}

public sealed class PartyRoleRemovalException : Exception
{
    public PartyRoleRemovalException() : base("Existing party roles cannot be removed from this workflow.") { }
}

public sealed class PartyAlreadySharedException : Exception
{
    public PartyAlreadySharedException() : base("The party is already assigned to the target branch.") { }
}

public sealed class PartyCommands(
    IFieldOpsDbContext dbContext,
    IMutationExecutor mutationExecutor,
    IAuditWriter auditWriter,
    IPartyNameLock partyNameLock)
{
    public Task<Guid> CreateAsync(CreatePartyInput input, CancellationToken cancellationToken = default) =>
        mutationExecutor.ExecuteAsync(
            "party-create",
            async token =>
            {
                Party party = Party.CreateOrganization(input.OrganizationName);
                string normalizedName = await partyNameLock.NormalizeAndAcquireAsync(
                    party.OrganizationName!,
                    token);
                if (await dbContext.Parties.AnyAsync(
                    party => EF.Property<string>(party, "NormalizedName") == normalizedName,
                    token))
                {
                    throw new PartyDuplicateException();
                }

                Branch branch = await dbContext.Branches.SingleOrDefaultAsync(
                    item => item.Id == input.BranchId,
                    token) ?? throw new KeyNotFoundException("Branch not found.");
                PartyRoleType roleType = input.RoleType
                    ?? throw new ArgumentException("A party role is required.", nameof(input));
                party.AddRole(roleType);
                party.AssignToBranch(branch);
                List<string> changedFields = [nameof(input.OrganizationName), nameof(input.RoleType), nameof(input.BranchId)];

                if (!string.IsNullOrWhiteSpace(input.ContactFirstName) &&
                    !string.IsNullOrWhiteSpace(input.ContactLastName))
                {
                    party.AddContact(input.ContactFirstName, input.ContactLastName, true);
                    changedFields.Add(nameof(input.ContactFirstName));
                    changedFields.Add(nameof(input.ContactLastName));
                }

                if (!string.IsNullOrWhiteSpace(input.SiteName))
                {
                    party.AddSite(branch, input.SiteName);
                    changedFields.Add(nameof(input.SiteName));
                }

                dbContext.Parties.Add(party);
                auditWriter.Write(nameof(Party), party.Id, input.BranchId, "Created", "Success", changedFields);
                return party.Id;
            },
            cancellationToken);

    public Task UpdateAsync(EditPartyInput input, CancellationToken cancellationToken = default) =>
        mutationExecutor.ExecuteAsync(
            "party-update",
            async token =>
            {
                Party party = await dbContext.Parties
                    .Include(item => item.Roles)
                    .Include(item => item.BranchAssignments)
                    .SingleOrDefaultAsync(item => item.Id == input.Id, token)
                    ?? throw new KeyNotFoundException("Party not found.");
                EnsureCurrentVersion(party, input.Version);
                if (!party.BranchAssignments.Any(assignment => assignment.BranchId == input.BranchId))
                {
                    throw new UnauthorizedAccessException("Party is not assigned to this branch.");
                }

                string candidateName = input.OrganizationName.Trim();
                string normalizedName = await partyNameLock.NormalizeAndAcquireAsync(candidateName, token);
                if (await dbContext.Parties.AnyAsync(
                    item => item.Id != input.Id && EF.Property<string>(item, "NormalizedName") == normalizedName,
                    token))
                {
                    throw new PartyDuplicateException();
                }

                List<string> changedFields = [];
                if (!string.Equals(party.OrganizationName, input.OrganizationName.Trim(), StringComparison.Ordinal))
                {
                    party.UpdateOrganizationName(input.OrganizationName);
                    changedFields.Add(nameof(input.OrganizationName));
                }

                bool isCustomer = party.Roles.Any(role => role.RoleType == FieldOps.Domain.Enums.PartyRoleType.Customer);
                bool isBusinessPartner = party.Roles.Any(role => role.RoleType == FieldOps.Domain.Enums.PartyRoleType.BusinessPartner);
                if (isCustomer && !input.IsCustomer || isBusinessPartner && !input.IsBusinessPartner)
                {
                    throw new PartyRoleRemovalException();
                }

                if (input.IsCustomer && !isCustomer)
                {
                    party.AddRole(FieldOps.Domain.Enums.PartyRoleType.Customer);
                    changedFields.Add(nameof(input.IsCustomer));
                }

                if (input.IsBusinessPartner && !isBusinessPartner)
                {
                    party.AddRole(FieldOps.Domain.Enums.PartyRoleType.BusinessPartner);
                    changedFields.Add(nameof(input.IsBusinessPartner));
                }

                auditWriter.Write(nameof(Party), party.Id, input.BranchId, "Updated", "Success", changedFields);
                return true;
            },
            cancellationToken);

    public Task ShareAsync(Guid partyId, SharePartyInput input, CancellationToken cancellationToken = default) =>
        mutationExecutor.ExecuteAsync(
            "party-share",
            async token =>
            {
                Guid targetBranchId = input.TargetBranchId
                    ?? throw new ArgumentException("A target branch is required.", nameof(input));
                Party party = await dbContext.Parties
                    .Include(item => item.BranchAssignments)
                    .SingleOrDefaultAsync(item => item.Id == partyId, token)
                    ?? throw new KeyNotFoundException("Party not found.");
                EnsureCurrentVersion(party, input.Version);
                if (!party.BranchAssignments.Any(assignment => assignment.BranchId == input.BranchId))
                {
                    throw new UnauthorizedAccessException("Party is not assigned to the acting branch.");
                }

                if (party.BranchAssignments.Any(assignment => assignment.BranchId == targetBranchId))
                {
                    throw new PartyAlreadySharedException();
                }

                Branch targetBranch = await dbContext.Branches.SingleOrDefaultAsync(
                    branch => branch.Id == targetBranchId,
                    token) ?? throw new KeyNotFoundException("Target branch not found.");
                party.AssignToBranch(targetBranch);
                auditWriter.Write(
                    nameof(Party),
                    party.Id,
                    input.BranchId,
                    "Shared",
                    "Success",
                    [nameof(input.TargetBranchId)]);
                return true;
            },
            cancellationToken);

    private static void EnsureCurrentVersion(Party party, uint expectedVersion)
    {
        if (party.Version != expectedVersion)
        {
            throw new PartyConcurrencyException();
        }
    }
}
