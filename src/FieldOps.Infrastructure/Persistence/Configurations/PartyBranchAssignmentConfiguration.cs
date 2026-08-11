using FieldOps.Domain.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldOps.Infrastructure.Persistence.Configurations;

internal sealed class PartyBranchAssignmentConfiguration : EntityConfiguration<PartyBranchAssignment>
{
    protected override void ConfigureEntity(EntityTypeBuilder<PartyBranchAssignment> builder)
    {
        builder.ToTable("PartyBranchAssignments");
        builder.HasIndex(assignment => new { assignment.PartyId, assignment.BranchId }).IsUnique();
        builder.HasOne<Branch>().WithMany().HasForeignKey(assignment => assignment.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}