using FieldOps.Domain.Entities;
using FieldOps.Features.Work;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldOps.Infrastructure.Persistence.Configurations;

internal sealed class PartyConfiguration : EntityConfiguration<Party>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Party> builder)
    {
        builder.ToTable("Parties");
        builder.Property(party => party.OrganizationName);
        builder.Property(party => party.FirstName);
        builder.Property(party => party.LastName);
        builder.Property<string>("NormalizedName")
            .HasComputedColumnSql("upper(COALESCE(\"OrganizationName\", \"LastName\" || ' ' || \"FirstName\"))", stored: true);
        builder.HasIndex("NormalizedName");
        builder.Property<string>(SearchTextNormalization.PropertyName)
            .HasComputedColumnSql(
                SearchTextNormalization.PostgresGeneratedExpression(
                    "COALESCE(\"OrganizationName\", \"LastName\" || ' ' || \"FirstName\")"),
                stored: true);

        builder.HasMany(party => party.Roles).WithOne().HasForeignKey(role => role.PartyId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(party => party.Roles).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasMany(party => party.BranchAssignments).WithOne().HasForeignKey(assignment => assignment.PartyId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(party => party.BranchAssignments).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasMany(party => party.Contacts).WithOne().HasForeignKey(contact => contact.PartyId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(party => party.Contacts).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasMany(party => party.Sites).WithOne().HasForeignKey(site => site.PartyId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(party => party.Sites).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}