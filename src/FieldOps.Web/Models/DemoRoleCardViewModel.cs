namespace FieldOps.Web.Models;

public sealed record DemoRoleCardViewModel(
    string Role,
    string DisplayName,
    string Description,
    string LoginToken);