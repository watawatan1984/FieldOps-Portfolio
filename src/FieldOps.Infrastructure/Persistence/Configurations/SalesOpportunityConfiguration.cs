using FieldOps.Domain.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldOps.Infrastructure.Persistence.Configurations;

internal sealed class SalesOpportunityConfiguration : EntityConfiguration<SalesOpportunity>
{
    protected override void ConfigureEntity(EntityTypeBuilder<SalesOpportunity> builder)
    {
        builder.ToTable("SalesOpportunities");
        builder.Property(opportunity => opportunity.ProposedAmount).HasPrecision(18, 2);
        builder.Property(opportunity => opportunity.ExpectedCloseDate).HasColumnType("date");
        builder.HasIndex(opportunity => new { opportunity.BranchId, opportunity.Status, opportunity.ExpectedCloseDate });
        builder.HasOne<Branch>().WithMany().HasForeignKey(opportunity => opportunity.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Party>().WithMany().HasForeignKey(opportunity => opportunity.PartyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Site>().WithMany().HasForeignKey(opportunity => opportunity.SiteId).OnDelete(DeleteBehavior.Restrict);
    }
}