using FieldOps.Domain.Common;
using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;

namespace FieldOps.Domain.Tests.Quotes;

public sealed class QuoteTests
{
    public static TheoryData<QuoteStatus, QuoteStatus> AllowedTransitions =>
        new()
        {
            { QuoteStatus.Draft, QuoteStatus.Issued },
            { QuoteStatus.Draft, QuoteStatus.Rejected },
            { QuoteStatus.Issued, QuoteStatus.Accepted },
            { QuoteStatus.Issued, QuoteStatus.Rejected },
            { QuoteStatus.Issued, QuoteStatus.Expired }
        };

    public static TheoryData<QuoteStatus, QuoteStatus> RejectedTransitions =>
        new()
        {
            { QuoteStatus.Draft, QuoteStatus.Accepted },
            { QuoteStatus.Draft, QuoteStatus.Expired },
            { QuoteStatus.Issued, QuoteStatus.Draft },
            { QuoteStatus.Accepted, QuoteStatus.Rejected },
            { QuoteStatus.Rejected, QuoteStatus.Issued },
            { QuoteStatus.Expired, QuoteStatus.Accepted }
        };

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void MoveTo_AllowsDocumentedTransitions(QuoteStatus current, QuoteStatus next)
    {
        Quote quote = CreateAt(current);

        quote.MoveTo(next, Utc(12));

        Assert.Equal(next, quote.Status);
    }

    [Theory]
    [MemberData(nameof(RejectedTransitions))]
    public void MoveTo_RejectsUndocumentedOrTerminalTransitions(QuoteStatus current, QuoteStatus next)
    {
        Quote quote = CreateAt(current);

        Assert.Throws<DomainException>(() => quote.MoveTo(next, Utc(12)));
    }

    [Fact]
    public void MoveTo_RequiresUtcTimestamp()
    {
        Quote quote = CreateAt(QuoteStatus.Draft);

        Assert.Throws<DomainException>(() => quote.MoveTo(QuoteStatus.Issued, new DateTime(2026, 8, 12)));
    }

    [Fact]
    public void MoveTo_IssuedRequiresLineItemsAndValidityDate()
    {
        Quote withoutLines = CreateDraft();
        withoutLines.SetValidUntil(Utc(30));

        Assert.Throws<DomainException>(() => withoutLines.MoveTo(QuoteStatus.Issued, Utc(12)));
        Assert.DoesNotContain(QuoteStatus.Issued, withoutLines.GetAllowedTransitions());

        Quote withoutValidity = CreateDraft();
        withoutValidity.AddLineItem("ねずみ防除 初回施工", "式", 1m, 48000m);

        Assert.Throws<DomainException>(() => withoutValidity.MoveTo(QuoteStatus.Issued, Utc(12)));
        Assert.DoesNotContain(QuoteStatus.Issued, withoutValidity.GetAllowedTransitions());
    }

    [Fact]
    public void MoveTo_IssuedRejectsValidityDateInThePast()
    {
        Quote quote = CreateDraft();
        quote.AddLineItem("ねずみ防除 初回施工", "式", 1m, 48000m);
        quote.SetValidUntil(Utc(10));

        Assert.Throws<DomainException>(() => quote.MoveTo(QuoteStatus.Issued, Utc(20)));
    }

    [Fact]
    public void MoveTo_IssuedRecordsTheIssueDate()
    {
        Quote quote = CreateAt(QuoteStatus.Draft);

        quote.MoveTo(QuoteStatus.Issued, Utc(12));

        Assert.Equal(Utc(12).Date, quote.IssuedOn);
    }

    [Fact]
    public void AddLineItem_RecalculatesSubtotalTaxAndTotal()
    {
        Quote quote = CreateDraft();

        quote.AddLineItem("ねずみ防除 初回施工", "式", 3m, 1500m);
        quote.AddLineItem("捕獲トラップ設置", "個", 1m, 333m);

        Assert.Equal(4833m, quote.Subtotal);
        Assert.Equal(483m, quote.TaxAmount);
        Assert.Equal(5316m, quote.TotalAmount);
    }

    [Fact]
    public void RemoveLineItem_ResequencesRemainingLinesAndRecalculates()
    {
        Quote quote = CreateDraft();
        quote.AddLineItem("初回施工", "式", 1m, 10000m);
        QuoteLineItem second = quote.AddLineItem("定期点検", "回", 2m, 5000m);
        quote.AddLineItem("報告書作成", "式", 1m, 3000m);

        quote.RemoveLineItem(second.Id);

        Assert.Equal(2, quote.LineItems.Count);
        Assert.Equal([1, 2], quote.LineItems.OrderBy(item => item.SortOrder).Select(item => item.SortOrder));
        Assert.Equal(13000m, quote.Subtotal);
        Assert.Equal(1300m, quote.TaxAmount);
        Assert.Equal(14300m, quote.TotalAmount);
    }

    [Fact]
    public void RemoveLineItem_RejectsAnUnknownLine()
    {
        Quote quote = CreateDraft();
        quote.AddLineItem("初回施工", "式", 1m, 10000m);

        Assert.Throws<DomainException>(() => quote.RemoveLineItem(Guid.NewGuid()));
    }

    [Fact]
    public void AddLineItem_RejectsInvalidQuantityPriceOrDescription()
    {
        Quote quote = CreateDraft();

        Assert.Throws<DomainException>(() => quote.AddLineItem("初回施工", "式", 0m, 10000m));
        Assert.Throws<DomainException>(() => quote.AddLineItem("初回施工", "式", 1m, -1m));
        Assert.Throws<DomainException>(() => quote.AddLineItem("   ", "式", 1m, 10000m));
        Assert.Throws<DomainException>(() => quote.AddLineItem("初回施工", "   ", 1m, 10000m));
    }

    [Fact]
    public void EditingIsRejectedOnceTheQuoteLeavesDraft()
    {
        Quote quote = CreateAt(QuoteStatus.Issued);

        Assert.Throws<DomainException>(() => quote.AddLineItem("追加作業", "式", 1m, 1000m));
        Assert.Throws<DomainException>(() => quote.ClearLineItems());
        Assert.Throws<DomainException>(() => quote.SetValidUntil(Utc(30)));
        Assert.Throws<DomainException>(() => quote.SetNotes("あとから追記"));
        Assert.Throws<DomainException>(() => quote.SetTaxRate(8m));
        Assert.Throws<DomainException>(() => quote.RemoveLineItem(quote.LineItems[0].Id));
    }

    [Fact]
    public void SetTaxRate_RecalculatesAndRejectsOutOfRangeRates()
    {
        Quote quote = CreateDraft();
        quote.AddLineItem("初回施工", "式", 1m, 10000m);

        quote.SetTaxRate(8m);

        Assert.Equal(800m, quote.TaxAmount);
        Assert.Equal(10800m, quote.TotalAmount);
        Assert.Throws<DomainException>(() => quote.SetTaxRate(-1m));
        Assert.Throws<DomainException>(() => quote.SetTaxRate(101m));
    }

    [Fact]
    public void SetNotes_TrimsAndTreatsBlankAsAbsent()
    {
        Quote quote = CreateDraft();

        quote.SetNotes("  夜間作業を含みます  ");
        Assert.Equal("夜間作業を含みます", quote.Notes);

        quote.SetNotes("   ");
        Assert.Null(quote.Notes);
    }

    [Fact]
    public void Create_RequiresTheOpportunityToMatchBranchPartyAndSite()
    {
        Branch branch = Branch.Create("Harbor Office");
        Party party = Party.CreateOrganization("Northwind Service Works");
        party.AssignToBranch(branch);
        party.AddSite(branch, "Pier 8 Workshop");
        Site site = party.Sites.Single();
        SalesOpportunity opportunity = SalesOpportunity.Create(branch, party, site);

        Party otherParty = Party.CreateOrganization("Contoso Facilities");
        otherParty.AssignToBranch(branch);
        otherParty.AddSite(branch, "Warehouse 3");
        Site otherSite = otherParty.Sites.Single();

        Assert.Throws<DomainException>(() => Quote.Create(branch, otherParty, otherSite, opportunity, "Q-2026-0001", 1, 10m));
    }

    [Fact]
    public void Create_RejectsBlankNumberNonPositiveRevisionAndOutOfRangeTaxRate()
    {
        Branch branch = Branch.Create("Harbor Office");
        Party party = Party.CreateOrganization("Northwind Service Works");
        party.AssignToBranch(branch);
        party.AddSite(branch, "Pier 8 Workshop");
        Site site = party.Sites.Single();
        SalesOpportunity opportunity = SalesOpportunity.Create(branch, party, site);

        Assert.Throws<DomainException>(() => Quote.Create(branch, party, site, opportunity, "  ", 1, 10m));
        Assert.Throws<DomainException>(() => Quote.Create(branch, party, site, opportunity, "Q-2026-0001", 0, 10m));
        Assert.Throws<DomainException>(() => Quote.Create(branch, party, site, opportunity, "Q-2026-0001", 1, 101m));
    }

    [Fact]
    public void AssignOwner_RequiresAStableIdentityId()
    {
        Quote quote = CreateDraft();

        quote.AssignOwner("sales-user-id");

        Assert.Equal("sales-user-id", quote.OwnerUserId);
        Assert.Throws<DomainException>(() => quote.AssignOwner(string.Empty));
    }

    private static Quote CreateAt(QuoteStatus status)
    {
        Quote quote = CreateDraft();
        quote.AddLineItem("ねずみ防除 初回施工", "式", 1m, 48000m);
        quote.SetValidUntil(Utc(30));

        foreach (QuoteStatus next in PathTo(status))
        {
            quote.MoveTo(next, Utc(11));
        }

        return quote;
    }

    private static Quote CreateDraft()
    {
        Branch branch = Branch.Create("Harbor Office");
        Party party = Party.CreateOrganization("Northwind Service Works");
        party.AssignToBranch(branch);
        party.AddSite(branch, "Pier 8 Workshop");
        Site site = party.Sites.Single();
        SalesOpportunity opportunity = SalesOpportunity.Create(branch, party, site);

        return Quote.Create(branch, party, site, opportunity, "Q-2026-0001", 1, 10m);
    }

    private static IEnumerable<QuoteStatus> PathTo(QuoteStatus status) => status switch
    {
        QuoteStatus.Draft => [],
        QuoteStatus.Issued => [QuoteStatus.Issued],
        QuoteStatus.Accepted => [QuoteStatus.Issued, QuoteStatus.Accepted],
        QuoteStatus.Rejected => [QuoteStatus.Rejected],
        QuoteStatus.Expired => [QuoteStatus.Issued, QuoteStatus.Expired],
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static DateTime Utc(int day) => new(2026, 8, day, 9, 0, 0, DateTimeKind.Utc);
}