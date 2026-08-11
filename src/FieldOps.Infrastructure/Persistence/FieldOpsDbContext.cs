using FieldOps.Domain.Entities;
using FieldOps.Features.Abstractions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FieldOps.Infrastructure.Persistence;

public sealed class FieldOpsDbContext(DbContextOptions<FieldOpsDbContext> options)
    : DbContext(options), IFieldOpsDbContext
{
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Party> Parties => Set<Party>();
    public DbSet<SalesOpportunity> SalesOpportunities => Set<SalesOpportunity>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FieldOpsDbContext).Assembly);
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