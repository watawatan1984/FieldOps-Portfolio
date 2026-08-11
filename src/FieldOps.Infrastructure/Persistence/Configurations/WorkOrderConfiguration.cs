using FieldOps.Domain.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldOps.Infrastructure.Persistence.Configurations;

internal sealed class WorkOrderConfiguration : EntityConfiguration<WorkOrder>
{
    protected override void ConfigureEntity(EntityTypeBuilder<WorkOrder> builder)
    {
        builder.ToTable("WorkOrders");
        builder.Property<DateTime?>("ScheduledStartUtc").HasColumnType("timestamp with time zone");
        builder.HasIndex([nameof(WorkOrder.BranchId), nameof(WorkOrder.Status), "ScheduledStartUtc"]);
        builder.HasIndex(workOrder => new { workOrder.PartyId, workOrder.SiteId });
        builder.HasOne<Branch>().WithMany().HasForeignKey(workOrder => workOrder.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Party>().WithMany().HasForeignKey(workOrder => workOrder.PartyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Site>().WithMany().HasForeignKey(workOrder => workOrder.SiteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(workOrder => workOrder.Events).WithOne().HasForeignKey(workEvent => workEvent.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(workOrder => workOrder.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}