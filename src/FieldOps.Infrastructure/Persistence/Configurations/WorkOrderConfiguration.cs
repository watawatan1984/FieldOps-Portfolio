using FieldOps.Domain.Entities;
using FieldOps.Infrastructure.Identity;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldOps.Infrastructure.Persistence.Configurations;

internal sealed class WorkOrderConfiguration : EntityConfiguration<WorkOrder>
{
    protected override void ConfigureEntity(EntityTypeBuilder<WorkOrder> builder)
    {
        builder.ToTable("WorkOrders");
        builder.Property(workOrder => workOrder.ScheduledStartUtc).HasColumnType("timestamp with time zone");
        builder.Property(workOrder => workOrder.AssignedUserId).HasMaxLength(450);
        builder.HasIndex(workOrder => new { workOrder.BranchId, workOrder.Status, workOrder.ScheduledStartUtc });
        builder.HasIndex(workOrder => new { workOrder.PartyId, workOrder.SiteId });
        builder.HasIndex(workOrder => workOrder.AssignedUserId);
        builder.HasOne<Branch>().WithMany().HasForeignKey(workOrder => workOrder.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Party>().WithMany().HasForeignKey(workOrder => workOrder.PartyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Site>().WithMany().HasForeignKey(workOrder => workOrder.SiteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(workOrder => workOrder.AssignedUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(workOrder => workOrder.Events).WithOne().HasForeignKey(workEvent => workEvent.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(workOrder => workOrder.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
