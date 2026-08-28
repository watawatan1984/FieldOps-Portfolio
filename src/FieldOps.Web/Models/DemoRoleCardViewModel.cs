namespace FieldOps.Web.Models;

public sealed record DemoRoleCardViewModel(
    string Role,
    string RoleLabel,
    string DisplayName,
    string Description,
    string LoginToken);