namespace FieldOps.Infrastructure.Demo;

public sealed class DemoModeOptions
{
    public const string SectionName = "DemoMode";
    public const string ApprovedDatasetIdentifier = "fieldops-portal-fictional-demo";
    public const string ApprovedDatasetVersion = "1";

    public bool Enabled { get; set; }

    public string DatasetIdentifier { get; set; } = string.Empty;

    public string DatasetVersion { get; set; } = string.Empty;

    public bool HasApprovedDatasetConfiguration =>
        !Enabled ||
        (string.Equals(DatasetIdentifier, ApprovedDatasetIdentifier, StringComparison.Ordinal) &&
         string.Equals(DatasetVersion, ApprovedDatasetVersion, StringComparison.Ordinal));
}