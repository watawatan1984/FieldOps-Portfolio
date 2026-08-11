using FieldOps.Domain.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldOps.Infrastructure.Persistence.Configurations;

internal sealed class PartyRoleConfiguration : EntityConfiguration<PartyRole>
{
    protected override void ConfigureEntity(EntityTypeBuilder<PartyRole> builder)
    {
        builder.ToTable("PartyRoles");
        builder.HasIndex(role => new { role.PartyId, role.RoleType }).IsUnique();
    }
}