using FieldOps.Domain.Common;
using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;
using FieldOps.Features.Abstractions;

using Microsoft.EntityFrameworkCore;

namespace FieldOps.Features.Quotes;

public sealed class QuoteConcurrencyException : Exception
{
    public QuoteConcurrencyException() : base("The quote was changed by another user.") { }
}

public sealed class QuoteCommands(
    IFieldOpsDbContext dbContext,
    IMutationExecutor mutationExecutor,
    IAuditWriter auditWriter,
    ICurrentUser currentUser,
    IFieldOpsUserDirectory userDirectory,
    TimeProvider timeProvider)
{
    private const string SalesRepresentativeRole = "Sales Representative";

    public Task<Guid> CreateAsync(QuoteEditInput input, CancellationToken cancellationToken = default) =>
        mutationExecutor.ExecuteAsync(
            "quote-create",
            async token =>
            {
                await ValidateOwnerAsync(input, token);

                SalesOpportunity opportunity = await dbContext.SalesOpportunities
                    .SingleOrDefaultAsync(item => item.Id == input.SalesOpportunityId, token)
                    ?? throw new KeyNotFoundException("Sales opportunity not found.");

                if (opportunity.BranchId != input.BranchId)
                {
                    throw new UnauthorizedAccessException("A quote must belong to the branch of its sales opportunity.");
                }

                Branch branch = await dbContext.Branches.SingleOrDefaultAsync(item => item.Id == opportunity.BranchId, token)
                    ?? throw new KeyNotFoundException("Branch not found.");
                Party party = await dbContext.Parties
                    .Include(item => item.BranchAssignments)
                    .Include(item => item.Sites)
                    .SingleOrDefaultAsync(item => item.Id == opportunity.PartyId, token)
                    ?? throw new KeyNotFoundException("Party not found.");
                Site site = party.Sites.SingleOrDefault(item => item.Id == opportunity.SiteId)
                    ?? throw new KeyNotFoundException("Site not found.");

                DateTime nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                int revisionNumber = await NextRevisionNumberAsync(opportunity.Id, token);
                string quoteNumber = await NextQuoteNumberAsync(nowUtc, token);

                Quote quote = Quote.Create(branch, party, site, opportunity, quoteNumber, revisionNumber, input.TaxRatePercent);
                quote.AssignOwner(input.OwnerUserId);
                ApplyEditableFields(quote, input);

                dbContext.Quotes.Add(quote);
                auditWriter.Write(
                    nameof(Quote),
                    quote.Id,
                    quote.BranchId,
                    "Created",
                    "Success",
                    [nameof(input.SalesOpportunityId), nameof(input.OwnerUserId), nameof(input.TaxRatePercent), nameof(input.ValidUntil), nameof(input.LineItems)]);
                return quote.Id;
            },
            cancellationToken);

    public async Task UpdateAsync(QuoteEditInput input, CancellationToken cancellationToken = default)
    {
        try
        {
            await mutationExecutor.ExecuteAsync(
                "quote-update",
                async token =>
                {
                    await ValidateOwnerAsync(input, token);

                    Quote quote = await LoadAsync(input.Id, token);
                    EnsureCurrentVersion(quote, input.Version);
                    EnsureMutationScope(quote);

                    if (quote.SalesOpportunityId != input.SalesOpportunityId)
                    {
                        throw new UnauthorizedAccessException("A quote sales opportunity cannot be changed.");
                    }

                    List<string> changedFields = [];
                    if (quote.OwnerUserId != input.OwnerUserId)
                    {
                        quote.AssignOwner(input.OwnerUserId);
                        changedFields.Add(nameof(input.OwnerUserId));
                    }

                    if (quote.TaxRatePercent != input.TaxRatePercent)
                    {
                        quote.SetTaxRate(input.TaxRatePercent);
                        changedFields.Add(nameof(input.TaxRatePercent));
                    }

                    if (quote.ValidUntil != input.ValidUntil?.Date)
                    {
                        quote.SetValidUntil(RequireValidUntil(input));
                        changedFields.Add(nameof(input.ValidUntil));
                    }

                    if (quote.Notes != NullIfWhiteSpace(input.Notes))
                    {
                        quote.SetNotes(input.Notes);
                        changedFields.Add(nameof(input.Notes));
                    }

                    if (LineItemsDiffer(quote, input))
                    {
                        ReplaceLineItems(quote, input);
                        changedFields.Add(nameof(input.LineItems));
                    }

                    auditWriter.Write(nameof(Quote), quote.Id, quote.BranchId, "Updated", "Success", changedFields);
                    return true;
                },
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new QuoteConcurrencyException();
        }
    }

    public async Task TransitionAsync(Guid id, QuoteTransitionInput input, CancellationToken cancellationToken = default)
    {
        try
        {
            await mutationExecutor.ExecuteAsync(
                "quote-transition",
                async token =>
                {
                    Quote quote = await LoadAsync(id, token);
                    EnsureCurrentVersion(quote, input.Version);
                    EnsureMutationScope(quote);

                    quote.MoveTo(input.NextStatus, timeProvider.GetUtcNow().UtcDateTime);

                    if (input.NextStatus == QuoteStatus.Issued)
                    {
                        await SynchroniseOpportunityProposalAsync(quote, token);
                    }

                    auditWriter.Write(nameof(Quote), quote.Id, quote.BranchId, "StatusChanged", "Success", [nameof(input.NextStatus)]);
                    return true;
                },
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new QuoteConcurrencyException();
        }
    }

    private async Task SynchroniseOpportunityProposalAsync(Quote quote, CancellationToken cancellationToken)
    {
        SalesOpportunity opportunity = await dbContext.SalesOpportunities
            .SingleOrDefaultAsync(item => item.Id == quote.SalesOpportunityId, cancellationToken)
            ?? throw new KeyNotFoundException("Sales opportunity not found.");

        if (quote.ValidUntil is null)
        {
            throw new DomainException("An issued quote must carry a validity date.");
        }

        opportunity.SetProposal(quote.TotalAmount, quote.ValidUntil.Value);
        auditWriter.Write(
            nameof(SalesOpportunity),
            opportunity.Id,
            opportunity.BranchId,
            "Updated",
            "Success",
            ["ProposedAmount", "ExpectedCloseDate"]);
    }

    private async Task<Quote> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await dbContext.Quotes
            .Include(quote => quote.LineItems)
            .SingleOrDefaultAsync(quote => quote.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Quote not found.");

    private async Task<int> NextRevisionNumberAsync(Guid salesOpportunityId, CancellationToken cancellationToken)
    {
        int highest = await dbContext.Quotes
            .Where(quote => quote.SalesOpportunityId == salesOpportunityId)
            .Select(quote => (int?)quote.RevisionNumber)
            .MaxAsync(cancellationToken) ?? 0;

        return highest + 1;
    }

    private async Task<string> NextQuoteNumberAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        string prefix = $"Q-{nowUtc.Year:0000}-";
        int issued = await dbContext.Quotes.CountAsync(quote => quote.QuoteNumber.StartsWith(prefix), cancellationToken);
        return $"{prefix}{issued + 1:0000}";
    }

    private async Task ValidateOwnerAsync(QuoteEditInput input, CancellationToken cancellationToken)
    {
        IReadOnlyList<FieldOpsUserOption> owners = await userDirectory.GetUsersInRoleAsync(input.BranchId, SalesRepresentativeRole, cancellationToken);
        if (!owners.Any(owner => owner.Id == input.OwnerUserId))
        {
            throw new DomainException("Select a sales owner in this branch.");
        }

        if (currentUser.Role == SalesRepresentativeRole && input.OwnerUserId != currentUser.UserId)
        {
            throw new UnauthorizedAccessException("Sales representatives can manage only their own quotes.");
        }
    }

    private void EnsureMutationScope(Quote quote)
    {
        if (currentUser.Role == SalesRepresentativeRole && quote.OwnerUserId != currentUser.UserId)
        {
            throw new UnauthorizedAccessException("Sales representatives can manage only their own quotes.");
        }
    }

    private static void ApplyEditableFields(Quote quote, QuoteEditInput input)
    {
        quote.SetValidUntil(RequireValidUntil(input));
        quote.SetNotes(input.Notes);
        ReplaceLineItems(quote, input);
    }

    private static void ReplaceLineItems(Quote quote, QuoteEditInput input)
    {
        if (input.LineItems.Count == 0)
        {
            throw new DomainException("A quote requires at least one line item.");
        }

        quote.ClearLineItems();
        foreach (QuoteLineItemInput lineItem in input.LineItems)
        {
            quote.AddLineItem(lineItem.Description, lineItem.UnitName, lineItem.Quantity, lineItem.UnitPrice);
        }
    }

    private static bool LineItemsDiffer(Quote quote, QuoteEditInput input)
    {
        QuoteLineItem[] existing = [.. quote.LineItems.OrderBy(item => item.SortOrder)];
        if (existing.Length != input.LineItems.Count)
        {
            return true;
        }

        for (int index = 0; index < existing.Length; index++)
        {
            QuoteLineItem current = existing[index];
            QuoteLineItemInput candidate = input.LineItems[index];
            if (current.Description != candidate.Description?.Trim() ||
                current.UnitName != candidate.UnitName?.Trim() ||
                current.Quantity != candidate.Quantity ||
                current.UnitPrice != candidate.UnitPrice)
            {
                return true;
            }
        }

        return false;
    }

    private static DateTime RequireValidUntil(QuoteEditInput input) =>
        input.ValidUntil ?? throw new DomainException("A quote validity date is required.");

    private static void EnsureCurrentVersion(Quote quote, uint expectedVersion)
    {
        if (quote.Version != expectedVersion) throw new QuoteConcurrencyException();
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}