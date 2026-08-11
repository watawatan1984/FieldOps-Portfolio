using FieldOps.Domain.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldOps.Infrastructure.Persistence.Configurations;

internal sealed class BranchConfiguration : EntityConfiguration<Branch>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("Branches");
        builder.Property(branch => branch.Name).IsRequired();
    }
}