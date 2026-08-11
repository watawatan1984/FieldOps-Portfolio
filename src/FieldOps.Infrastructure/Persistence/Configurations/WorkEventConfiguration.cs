using FieldOps.Domain.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldOps.Infrastructure.Persistence.Configurations;

internal sealed class WorkEventConfiguration : EntityConfiguration<WorkEvent>
{
    protected override void ConfigureEntity(EntityTypeBuilder<WorkEvent> builder)
    {
        builder.ToTable("WorkEvents");
        builder.Property(workEvent => workEvent.EventType);
        builder.Property(workEvent => workEvent.OccurredAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(workEvent => workEvent.Summary).IsRequired();
        builder.Property(workEvent => workEvent.ActorUserId).IsRequired();
        builder.HasIndex(workEvent => new { workEvent.WorkOrderId, workEvent.OccurredAtUtc }).IsDescending(false, true);
        builder.HasOne<Branch>().WithMany().HasForeignKey(workEvent => workEvent.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}