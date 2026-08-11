using FieldOps.Infrastructure.Demo;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldOps.Infrastructure.Persistence.Configurations;

internal sealed class DemoResetExecutionConfiguration : IEntityTypeConfiguration<DemoResetExecution>
{
    public void Configure(EntityTypeBuilder<DemoResetExecution> builder)
    {
        builder.ToTable("DemoResetExecutions");
        builder.HasKey(execution => execution.Id);
        builder.Property(execution => execution.Id).ValueGeneratedNever();
        builder.Property(execution => execution.IdempotencyKey).HasMaxLength(64).IsRequired();
        builder.HasIndex(execution => execution.IdempotencyKey).IsUnique();
        builder.Property(execution => execution.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(execution => execution.StartedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(execution => execution.CompletedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(execution => execution.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(execution => execution.ActorUserId).HasMaxLength(450).IsRequired();
        builder.Property(execution => execution.Outcome).HasMaxLength(32).IsRequired();
    }
}