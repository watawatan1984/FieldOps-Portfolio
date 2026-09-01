using FieldOps.Domain.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldOps.Infrastructure.Persistence.Configurations;

internal sealed class QuoteLineItemConfiguration : EntityConfiguration<QuoteLineItem>
{
    protected override void ConfigureEntity(EntityTypeBuilder<QuoteLineItem> builder)
    {
        builder.ToTable("QuoteLineItems");
        builder.Property(lineItem => lineItem.Description).IsRequired().HasMaxLength(200);
        builder.Property(lineItem => lineItem.UnitName).IsRequired().HasMaxLength(16);
        builder.Property(lineItem => lineItem.Quantity).HasPrecision(18, 2);
        builder.Property(lineItem => lineItem.UnitPrice).HasPrecision(18, 2);
        builder.Ignore(lineItem => lineItem.Amount);
        builder.HasIndex(lineItem => new { lineItem.QuoteId, lineItem.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_QuoteLineItems_QuoteId_SortOrder");
    }
}