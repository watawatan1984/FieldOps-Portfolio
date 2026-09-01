using FieldOps.Domain.Entities;
using FieldOps.Infrastructure.Identity;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldOps.Infrastructure.Persistence.Configurations;

internal sealed class QuoteConfiguration : EntityConfiguration<Quote>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Quote> builder)
    {
        builder.ToTable("Quotes");
        builder.Property(quote => quote.QuoteNumber).IsRequired().HasMaxLength(32);
        builder.Property(quote => quote.TaxRatePercent).HasPrecision(5, 2);
        builder.Property(quote => quote.Subtotal).HasPrecision(18, 2);
        builder.Property(quote => quote.TaxAmount).HasPrecision(18, 2);
        builder.Property(quote => quote.TotalAmount).HasPrecision(18, 2);
        builder.Property(quote => quote.IssuedOn).HasColumnType("date");
        builder.Property(quote => quote.ValidUntil).HasColumnType("date");
        builder.Property(quote => quote.Notes).HasMaxLength(2000);
        builder.Property(quote => quote.OwnerUserId).HasMaxLength(450);
        builder.HasIndex(quote => new { quote.BranchId, quote.Status, quote.ValidUntil });
        builder.HasIndex(quote => new { quote.PartyId, quote.SiteId });
        builder.HasIndex(quote => quote.OwnerUserId);
        builder.HasIndex(quote => quote.QuoteNumber)
            .IsUnique()
            .HasDatabaseName("UX_Quotes_QuoteNumber");
        builder.HasIndex(quote => new { quote.SalesOpportunityId, quote.RevisionNumber })
            .IsUnique()
            .HasDatabaseName("UX_Quotes_SalesOpportunityId_RevisionNumber");
        builder.HasOne<Branch>().WithMany().HasForeignKey(quote => quote.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Party>().WithMany().HasForeignKey(quote => quote.PartyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Site>().WithMany().HasForeignKey(quote => quote.SiteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SalesOpportunity>().WithMany().HasForeignKey(quote => quote.SalesOpportunityId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(quote => quote.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(quote => quote.LineItems).WithOne().HasForeignKey(lineItem => lineItem.QuoteId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(quote => quote.LineItems).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}