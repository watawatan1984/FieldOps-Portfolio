using FieldOps.Domain.Entities;
using FieldOps.Features.Abstractions;
using FieldOps.Infrastructure.Identity;

using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FieldOps.Infrastructure.Persistence;

public sealed class FieldOpsDbContext(DbContextOptions<FieldOpsDbContext> options)
    : IdentityDbContext<ApplicationUser>(options), IFieldOpsDbContext
{
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Party> Parties => Set<Party>();
    public DbSet<SalesOpportunity> SalesOpportunities => Set<SalesOpportunity>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FieldOpsDbContext).Assembly);
        modelBuilder.Entity<ApplicationUser>(builder =>
        {
            builder.Property(user => user.DisplayName).HasMaxLength(200);
            builder.HasOne<Branch>()
                .WithMany()
                .HasForeignKey(user => user.BranchId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class FieldOpsDbContextFactory : IDesignTimeDbContextFactory<FieldOpsDbContext>
{
    public FieldOpsDbContext CreateDbContext(string[] args)
    {
        DbContextOptions<FieldOpsDbContext> options = new DbContextOptionsBuilder<FieldOpsDbContext>()
            .UseNpgsql()
            .Options;

        return new FieldOpsDbContext(options);
    }
}
