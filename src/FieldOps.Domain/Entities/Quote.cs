using FieldOps.Domain.Common;
using FieldOps.Domain.Enums;

namespace FieldOps.Domain.Entities;

public sealed class Quote : Entity
{
    private readonly List<QuoteLineItem> _lineItems = [];

    private Quote()
    {
        QuoteNumber = string.Empty;
    }

    private Quote(Branch branch, Party party, Site site, SalesOpportunity salesOpportunity, string quoteNumber, int revisionNumber, decimal taxRatePercent)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(salesOpportunity);
        EnsureOpportunityMatches(branch, party, site, salesOpportunity);

        BranchId = branch.Id;
        PartyId = party.Id;
        SiteId = site.Id;
        SalesOpportunityId = salesOpportunity.Id;
        QuoteNumber = RequiredText(quoteNumber, nameof(quoteNumber));
        RevisionNumber = RequireRevisionNumber(revisionNumber);
        TaxRatePercent = RequireTaxRate(taxRatePercent);
        Status = QuoteStatus.Draft;
    }

    public Guid BranchId { get; }

    public Guid PartyId { get; }

    public Guid SiteId { get; }

    public Guid SalesOpportunityId { get; }

    public string QuoteNumber { get; }

    public int RevisionNumber { get; }

    public string? OwnerUserId { get; private set; }

    public QuoteStatus Status { get; private set; }

    public decimal TaxRatePercent { get; private set; }

    public decimal Subtotal { get; private set; }

    public decimal TaxAmount { get; private set; }

    public decimal TotalAmount { get; private set; }

    public DateTime? IssuedOn { get; private set; }

    public DateTime? ValidUntil { get; private set; }

    public string? Notes { get; private set; }

    public IReadOnlyList<QuoteLineItem> LineItems => _lineItems.AsReadOnly();

    public static Quote Create(Branch branch, Party party, Site site, SalesOpportunity salesOpportunity, string quoteNumber, int revisionNumber, decimal taxRatePercent) =>
        new(branch, party, site, salesOpportunity, quoteNumber, revisionNumber, taxRatePercent);

    public void AssignOwner(string applicationUserId)
    {
        OwnerUserId = RequiredText(applicationUserId, nameof(applicationUserId));
        Touch();
    }

    public void SetTaxRate(decimal taxRatePercent)
    {
        RequireDraft("change the tax rate");
        TaxRatePercent = RequireTaxRate(taxRatePercent);
        Recalculate();
    }

    public void SetValidUntil(DateTime validUntil)
    {
        RequireDraft("change the validity date");

        if (validUntil == default)
        {
            throw new DomainException("A quote validity date is required.");
        }

        ValidUntil = validUntil.Date;
        Touch();
    }

    public void SetNotes(string? notes)
    {
        RequireDraft("change the notes");
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        Touch();
    }

    public QuoteLineItem AddLineItem(string description, string unitName, decimal quantity, decimal unitPrice)
    {
        RequireDraft("add a line item");

        QuoteLineItem lineItem = new(Id, _lineItems.Count + 1, description, unitName, quantity, unitPrice);
        _lineItems.Add(lineItem);
        Recalculate();
        return lineItem;
    }

    public void RemoveLineItem(Guid lineItemId)
    {
        RequireDraft("remove a line item");

        QuoteLineItem? lineItem = _lineItems.SingleOrDefault(item => item.Id == lineItemId)
            ?? throw new DomainException("The quote line item was not found on this quote.");

        _lineItems.Remove(lineItem);
        Resequence();
        Recalculate();
    }

    public void ClearLineItems()
    {
        RequireDraft("clear line items");
        _lineItems.Clear();
        Recalculate();
    }

    public void MoveTo(QuoteStatus next, DateTime occurredAtUtc)
    {
        RequireUtc(occurredAtUtc, "quote transition timestamp");

        if (!Enum.IsDefined(next) || !IsAllowedTransition(Status, next))
        {
            throw InvalidTransition(Status, next);
        }

        if (next == QuoteStatus.Issued)
        {
            if (_lineItems.Count == 0)
            {
                throw new DomainException($"Quote transition from {Status} to {next} requires at least one line item.");
            }

            if (ValidUntil is null)
            {
                throw new DomainException($"Quote transition from {Status} to {next} requires a validity date.");
            }

            if (ValidUntil < occurredAtUtc.Date)
            {
                throw new DomainException($"Quote transition from {Status} to {next} requires a validity date that is not in the past.");
            }

            IssuedOn = occurredAtUtc.Date;
        }

        Status = next;
        Touch();
    }

    public IReadOnlyList<QuoteStatus> GetAllowedTransitions() =>
        Enum.GetValues<QuoteStatus>()
            .Where(next => IsAllowedTransition(Status, next) &&
                (next != QuoteStatus.Issued || _lineItems.Count > 0 && ValidUntil is not null))
            .ToArray();

    private void Recalculate()
    {
        Subtotal = _lineItems.Sum(item => item.Amount);
        TaxAmount = decimal.Floor(Subtotal * TaxRatePercent / 100m);
        TotalAmount = Subtotal + TaxAmount;
        Touch();
    }

    private void Resequence()
    {
        int sortOrder = 1;
        foreach (QuoteLineItem lineItem in _lineItems.OrderBy(item => item.SortOrder).ToArray())
        {
            lineItem.Reorder(sortOrder);
            sortOrder++;
        }
    }

    private void RequireDraft(string attemptedChange)
    {
        if (Status != QuoteStatus.Draft)
        {
            throw new DomainException($"A quote in {Status} may not {attemptedChange}.");
        }
    }

    private static bool IsAllowedTransition(QuoteStatus current, QuoteStatus next) =>
        (current, next) switch
        {
            (QuoteStatus.Draft, QuoteStatus.Issued or QuoteStatus.Rejected) => true,
            (QuoteStatus.Issued, QuoteStatus.Accepted or QuoteStatus.Rejected or QuoteStatus.Expired) => true,
            _ => false
        };

    private static int RequireRevisionNumber(int revisionNumber) =>
        revisionNumber >= 1
            ? revisionNumber
            : throw new DomainException("A quote revision number must be greater than zero.");

    private static decimal RequireTaxRate(decimal taxRatePercent) =>
        taxRatePercent is >= 0m and <= 100m
            ? taxRatePercent
            : throw new DomainException("A quote tax rate must be between 0 and 100 percent.");

    private static void EnsureOpportunityMatches(Branch branch, Party party, Site site, SalesOpportunity salesOpportunity)
    {
        if (salesOpportunity.BranchId != branch.Id ||
            salesOpportunity.PartyId != party.Id ||
            salesOpportunity.SiteId != site.Id)
        {
            throw new DomainException("A quote must belong to the same branch, party, and site as its sales opportunity.");
        }
    }

    private static DomainException InvalidTransition(QuoteStatus current, QuoteStatus requested) =>
        new($"Quote transition from {current} to {requested} is not allowed.");

    private static void RequireUtc(DateTime value, string fieldName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new DomainException($"The {fieldName} must use UTC.");
        }
    }
}