using FieldOps.Infrastructure.Identity;

namespace FieldOps.Infrastructure.Demo;

public static class DemoDataManifest
{
    public static readonly Guid DatasetMarkerId = Guid.Parse("90000000-0000-4000-8000-000000000001");
    public static readonly DateTime EpochUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public const int BranchCount = 5;
    public const int PartyCount = 250;
    public const int SalesOpportunityCount = 250;
    public const int QuoteCount = 248;
    public const int WorkOrderCount = 250;
    public const int WorkEventCount = 750;
    public const int DemoUserCount = 4;
    public const int SeedAuditEntryCount = 20;

    public static IReadOnlyList<DemoBranch> Branches { get; } =
    [
        new(Guid.Parse("00000000-0000-4000-8000-000000000001"), "中央サービス支店"),
        new(Guid.Parse("00000000-0000-4000-8000-000000000002"), "現場サービス支店"),
        new(Guid.Parse("00000000-0000-4000-8000-000000000003"), "北部サービス支店"),
        new(Guid.Parse("00000000-0000-4000-8000-000000000004"), "南部サービス支店"),
        new(Guid.Parse("00000000-0000-4000-8000-000000000005"), "西部サービス支店")
    ];

    public static IReadOnlyDictionary<string, DemoUser> UsersByRole { get; } =
        new Dictionary<string, DemoUser>(StringComparer.Ordinal)
        {
            [DemoRoleNames.SystemAdministrator] = new(
                "60000000-0000-4000-8000-000000000001",
                "system.admin@fieldops.demo",
                "佐藤 健一",
                null,
                "61000000-0000-4000-8000-000000000001",
                "62000000-0000-4000-8000-000000000001"),
            [DemoRoleNames.BranchManager] = new(
                "60000000-0000-4000-8000-000000000002",
                "branch.manager@fieldops.demo",
                "鈴木 美咲",
                Branches[0].Id,
                "61000000-0000-4000-8000-000000000002",
                "62000000-0000-4000-8000-000000000002"),
            [DemoRoleNames.SalesRepresentative] = new(
                "60000000-0000-4000-8000-000000000003",
                "sales.rep@fieldops.demo",
                "高橋 翔太",
                Branches[0].Id,
                "61000000-0000-4000-8000-000000000003",
                "62000000-0000-4000-8000-000000000003"),
            [DemoRoleNames.FieldTechnician] = new(
                "60000000-0000-4000-8000-000000000004",
                "field.tech@fieldops.demo",
                "田中 葵",
                Branches[1].Id,
                "61000000-0000-4000-8000-000000000004",
                "62000000-0000-4000-8000-000000000004")
        };

    public static IReadOnlyDictionary<string, string> RoleIds { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DemoRoleNames.SystemAdministrator] = "70000000-0000-4000-8000-000000000001",
            [DemoRoleNames.BranchManager] = "70000000-0000-4000-8000-000000000002",
            [DemoRoleNames.SalesRepresentative] = "70000000-0000-4000-8000-000000000003",
            [DemoRoleNames.FieldTechnician] = "70000000-0000-4000-8000-000000000004"
        };

    public static Guid PartyId(int number) => NumberedGuid("10000000-0000-4000-8000-", number);

    public static Guid SalesOpportunityId(int number) => NumberedGuid("20000000-0000-4000-8000-", number);

    public static Guid QuoteId(int number) => NumberedGuid("21000000-0000-4000-8000-", number);

    public static Guid QuoteLineItemId(int number) => NumberedGuid("22000000-0000-4000-8000-", number);

    public static Guid WorkOrderId(int number) => NumberedGuid("30000000-0000-4000-8000-", number);

    public static Guid WorkEventId(int number) => NumberedGuid("40000000-0000-4000-8000-", number);

    private static Guid NumberedGuid(string prefix, int number) =>
        Guid.Parse($"{prefix}{number:000000000000}");
}

public sealed record DemoBranch(Guid Id, string Name);

public sealed record DemoUser(
    string Id,
    string UserName,
    string DisplayName,
    Guid? BranchId,
    string SecurityStamp,
    string ConcurrencyStamp);