using FieldOps.Infrastructure.Demo;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldOps.Infrastructure.Persistence.Configurations;

internal sealed class DemoDatasetMarkerConfiguration : IEntityTypeConfiguration<DemoDatasetMarker>
{
    public void Configure(EntityTypeBuilder<DemoDatasetMarker> builder)
    {
        builder.ToTable("DemoDatasetMarkers");
        builder.HasKey(marker => marker.Id);
        builder.Property(marker => marker.Id).ValueGeneratedNever();
        builder.Property(marker => marker.DatasetIdentifier).HasMaxLength(100).IsRequired();
        builder.Property(marker => marker.DatasetVersion).HasMaxLength(32).IsRequired();
        builder.Property(marker => marker.InstalledAtUtc).HasColumnType("timestamp with time zone");
    }
}