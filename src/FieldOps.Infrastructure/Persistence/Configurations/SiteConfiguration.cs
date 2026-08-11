using FieldOps.Domain.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldOps.Infrastructure.Persistence.Configurations;

internal sealed class SiteConfiguration : EntityConfiguration<Site>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Site> builder)
    {
        builder.ToTable("Sites");
        builder.Property(site => site.Name).IsRequired();
        builder.HasOne<Branch>().WithMany().HasForeignKey(site => site.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}