using FieldOps.Domain.Common;

namespace FieldOps.Domain.Entities;

public sealed class QuoteLineItem : Entity
{
    private QuoteLineItem()
    {
        Description = string.Empty;
        UnitName = string.Empty;
    }

    internal QuoteLineItem(Guid quoteId, int sortOrder, string description, string unitName, decimal quantity, decimal unitPrice)
    {
        if (quoteId == Guid.Empty)
        {
            throw new DomainException("A quote line item must belong to a quote.");
        }

        if (sortOrder < 1)
        {
            throw new DomainException("A quote line item sort order must be greater than zero.");
        }

        QuoteId = quoteId;
        SortOrder = sortOrder;
        Description = RequiredText(description, nameof(description));
        UnitName = RequiredText(unitName, nameof(unitName));
        Quantity = RequirePositiveQuantity(quantity);
        UnitPrice = RequireNonNegativePrice(unitPrice);
    }

    public Guid QuoteId { get; }

    public int SortOrder { get; private set; }

    public string Description { get; private set; }

    public string UnitName { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal Amount => decimal.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);

    internal void Reorder(int sortOrder)
    {
        if (sortOrder < 1)
        {
            throw new DomainException("A quote line item sort order must be greater than zero.");
        }

        SortOrder = sortOrder;
        Touch();
    }

    private static decimal RequirePositiveQuantity(decimal quantity)
    {
        if (quantity <= 0)
        {
            throw new DomainException("A quote line item quantity must be greater than zero.");
        }

        return quantity;
    }

    private static decimal RequireNonNegativePrice(decimal unitPrice)
    {
        if (unitPrice < 0)
        {
            throw new DomainException("A quote line item unit price must not be negative.");
        }

        return unitPrice;
    }
}