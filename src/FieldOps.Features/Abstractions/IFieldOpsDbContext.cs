using FieldOps.Domain.Entities;

using Microsoft.EntityFrameworkCore;

namespace FieldOps.Features.Abstractions;

public interface IFieldOpsDbContext
{
    DbSet<AuditEntry> AuditEntries { get; }
    DbSet<Branch> Branches { get; }
    DbSet<Party> Parties { get; }
    DbSet<SalesOpportunity> SalesOpportunities { get; }
    DbSet<WorkOrder> WorkOrders { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}