using FieldOps.Domain.Common;
using FieldOps.Domain.Entities;
using FieldOps.Features.Abstractions;

using Microsoft.EntityFrameworkCore;

namespace FieldOps.Features.Sales;

public sealed class SalesConcurrencyException : Exception
{
    public SalesConcurrencyException() : base("The sales opportunity was changed by another user.") { }
}

public sealed class SalesCommands(
    IFieldOpsDbContext dbContext,
    IMutationExecutor mutationExecutor,
    IAuditWriter auditWriter,
    ICurrentUser currentUser,
    IFieldOpsUserDirectory userDirectory,
    TimeProvider timeProvider)
{
    private const string SalesRepresentativeRole = "Sales Representative";
    private const string FieldTechnicianRole = "Field Technician";

    public Task<Guid> CreateAsync(SalesEditInput input, CancellationToken cancellationToken = default) =>
        mutationExecutor.ExecuteAsync(
            "sales-opportunity-create",
            async token =>
            {
                await ValidateUsersAsync(input, token);
                Branch branch = await dbContext.Branches.SingleOrDefaultAsync(item => item.Id == input.BranchId, token)
                    ?? throw new KeyNotFoundException("Branch not found.");
                Party party = await dbContext.Parties
                    .Include(item => item.BranchAssignments)
                    .Include(item => item.Sites)
                    .SingleOrDefaultAsync(item => item.Id == input.PartyId, token)
                    ?? throw new KeyNotFoundException("Party not found.");
                Site site = party.Sites.SingleOrDefault(item => item.Id == input.SiteId)
                    ?? throw new KeyNotFoundException("Site not found.");
                SalesOpportunity opportunity = SalesOpportunity.Create(branch, party, site);
                opportunity.AssignOwner(input.OwnerUserId);
                if (!string.IsNullOrWhiteSpace(input.AssignedUserId))
                {
                    opportunity.AssignToUser(input.AssignedUserId);
                }
                ApplyProposal(opportunity, input.ProposedAmount, input.ExpectedCloseDate);
                dbContext.SalesOpportunities.Add(opportunity);
                List<string> changedFields =
                [
                    nameof(input.BranchId), nameof(input.PartyId), nameof(input.SiteId), nameof(input.OwnerUserId)
                ];
                if (!string.IsNullOrWhiteSpace(input.AssignedUserId)) changedFields.Add(nameof(input.AssignedUserId));
                if (input.ProposedAmount is not null) changedFields.Add(nameof(input.ProposedAmount));
                if (input.ExpectedCloseDate is not null) changedFields.Add(nameof(input.ExpectedCloseDate));
                auditWriter.Write(nameof(SalesOpportunity), opportunity.Id, opportunity.BranchId, "Created", "Success", changedFields);
                return opportunity.Id;
            },
            cancellationToken);

    public async Task UpdateAsync(SalesEditInput input, CancellationToken cancellationToken = default)
    {
        try
        {
            await mutationExecutor.ExecuteAsync(
                "sales-opportunity-update",
                async token =>
                {
                    await ValidateUsersAsync(input, token);
                    SalesOpportunity opportunity = await dbContext.SalesOpportunities.SingleOrDefaultAsync(item => item.Id == input.Id, token)
                        ?? throw new KeyNotFoundException("Sales opportunity not found.");
                    EnsureCurrentVersion(opportunity, input.Version);
                    EnsureMutationScope(opportunity);
                    if (opportunity.BranchId != input.BranchId || opportunity.PartyId != input.PartyId || opportunity.SiteId != input.SiteId)
                    {
                        throw new UnauthorizedAccessException("A sales opportunity branch, party, and site cannot be changed.");
                    }
                    if (opportunity.ProposedAmount is not null &&
                        opportunity.ExpectedCloseDate is not null &&
                        input.ProposedAmount is null &&
                        input.ExpectedCloseDate is null)
                    {
                        throw new DomainException("An existing sales opportunity proposal cannot be cleared.");
                    }

                    List<string> changedFields = [];
                    if (opportunity.OwnerUserId != input.OwnerUserId)
                    {
                        opportunity.AssignOwner(input.OwnerUserId);
                        changedFields.Add(nameof(input.OwnerUserId));
                    }
                    if (opportunity.AssignedUserId != NullIfWhiteSpace(input.AssignedUserId))
                    {
                        opportunity.UpdateAssignedUser(input.AssignedUserId);
                        changedFields.Add(nameof(input.AssignedUserId));
                    }
                    if (opportunity.ProposedAmount != input.ProposedAmount || opportunity.ExpectedCloseDate != input.ExpectedCloseDate?.Date)
                    {
                        ApplyProposal(opportunity, input.ProposedAmount, input.ExpectedCloseDate);
                        changedFields.Add(nameof(input.ProposedAmount));
                        changedFields.Add(nameof(input.ExpectedCloseDate));
                    }
                    auditWriter.Write(nameof(SalesOpportunity), opportunity.Id, opportunity.BranchId, "Updated", "Success", changedFields);
                    return true;
                },
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new SalesConcurrencyException();
        }
    }

    public async Task TransitionAsync(Guid id, SalesTransitionInput input, CancellationToken cancellationToken = default)
    {
        try
        {
            await mutationExecutor.ExecuteAsync(
                "sales-opportunity-transition",
                async token =>
                {
                    SalesOpportunity opportunity = await dbContext.SalesOpportunities.SingleOrDefaultAsync(item => item.Id == id, token)
                        ?? throw new KeyNotFoundException("Sales opportunity not found.");
                    EnsureCurrentVersion(opportunity, input.Version);
                    EnsureMutationScope(opportunity);
                    opportunity.MoveTo(input.NextStatus, timeProvider.GetUtcNow().UtcDateTime);
                    auditWriter.Write(nameof(SalesOpportunity), opportunity.Id, opportunity.BranchId, "StatusChanged", "Success", [nameof(input.NextStatus)]);
                    return true;
                },
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new SalesConcurrencyException();
        }
    }

    private async Task ValidateUsersAsync(SalesEditInput input, CancellationToken cancellationToken)
    {
        IReadOnlyList<FieldOpsUserOption> owners = await userDirectory.GetUsersInRoleAsync(input.BranchId, SalesRepresentativeRole, cancellationToken);
        if (!owners.Any(owner => owner.Id == input.OwnerUserId))
        {
            throw new DomainException("Select a sales owner in this branch.");
        }
        if (currentUser.Role == SalesRepresentativeRole && input.OwnerUserId != currentUser.UserId)
        {
            throw new UnauthorizedAccessException("Sales representatives can manage only their own opportunities.");
        }
        if (!string.IsNullOrWhiteSpace(input.AssignedUserId))
        {
            IReadOnlyList<FieldOpsUserOption> technicians = await userDirectory.GetUsersInRoleAsync(input.BranchId, FieldTechnicianRole, cancellationToken);
            if (!technicians.Any(user => user.Id == input.AssignedUserId))
            {
                throw new DomainException("Select a technician in this branch.");
            }
        }
    }

    private void EnsureMutationScope(SalesOpportunity opportunity)
    {
        if (currentUser.Role == SalesRepresentativeRole && opportunity.OwnerUserId != currentUser.UserId)
        {
            throw new UnauthorizedAccessException("Sales representatives can manage only their own opportunities.");
        }
    }

    private static void ApplyProposal(SalesOpportunity opportunity, decimal? amount, DateTime? expectedCloseDate)
    {
        if (amount is null && expectedCloseDate is null) return;
        if (amount is null || expectedCloseDate is null)
        {
            throw new DomainException("Proposal amount and expected close date must be provided together.");
        }
        opportunity.SetProposal(amount.Value, expectedCloseDate.Value);
    }

    private static void EnsureCurrentVersion(SalesOpportunity opportunity, uint expectedVersion)
    {
        if (opportunity.Version != expectedVersion) throw new SalesConcurrencyException();
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}