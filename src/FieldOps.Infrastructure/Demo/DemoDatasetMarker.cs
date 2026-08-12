namespace FieldOps.Infrastructure.Demo;

public sealed class DemoDatasetMarker
{
    private DemoDatasetMarker()
    {
    }

    public Guid Id { get; private set; }

    public string DatasetIdentifier { get; private set; } = string.Empty;

    public string DatasetVersion { get; private set; } = string.Empty;

    public DateTime InstalledAtUtc { get; private set; }
}