using FieldOps.Domain.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldOps.Infrastructure.Persistence.Configurations;

internal sealed class ContactConfiguration : EntityConfiguration<Contact>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Contact> builder)
    {
        builder.ToTable("Contacts");
        builder.Property(contact => contact.FirstName).IsRequired();
        builder.Property(contact => contact.LastName).IsRequired();
        builder.Property(contact => contact.IsPrimary);
    }
}