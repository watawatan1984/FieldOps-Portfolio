using FieldOps.Domain.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldOps.Infrastructure.Persistence.Configurations;

internal sealed class AuditEntryConfiguration : EntityConfiguration<AuditEntry>
{
    protected override void ConfigureEntity(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("AuditEntries");
        builder.Property(audit => audit.AggregateType).IsRequired();
        builder.Property(audit => audit.AggregateId);
        builder.Property(audit => audit.BranchId);
        builder.Property(audit => audit.Action).IsRequired();
        builder.Property(audit => audit.Outcome).IsRequired();
        builder.Property(audit => audit.ChangeSummary).IsRequired();
        builder.Property(audit => audit.OccurredAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(audit => audit.ActorUserId).IsRequired();
        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(audit => audit.BranchId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(audit => new { audit.OccurredAtUtc, audit.Id }).IsDescending();
        builder.HasIndex(audit => new { audit.BranchId, audit.OccurredAtUtc, audit.Id })
            .IsDescending(false, true, true);
    }
}