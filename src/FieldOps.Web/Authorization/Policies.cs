using FieldOps.Infrastructure.Identity;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace FieldOps.Web.Authorization;

public static class Policies
{
    public const string ViewDashboard = nameof(ViewDashboard);
    public const string ManageParties = nameof(ManageParties);
    public const string ReadSales = nameof(ReadSales);
    public const string ManageSales = nameof(ManageSales);
    public const string ReadWorkOrders = nameof(ReadWorkOrders);
    public const string ManageWorkOrders = nameof(ManageWorkOrders);
    public const string UpdateWorkOrders = nameof(UpdateWorkOrders);
    public const string ViewAudit = nameof(ViewAudit);
    public const string ResetDemo = nameof(ResetDemo);

    public static IServiceCollection AddFieldOpsAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, BranchAccessHandler>();
        services.AddScoped<IFieldOpsResourceAuthorizer, FieldOpsResourceAuthorizer>();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build())
            .AddPolicy(ViewDashboard, policy => policy.RequireRole(DemoRoleNames.All))
            .AddPolicy(ManageParties, policy => policy.RequireRole(
                DemoRoleNames.SystemAdministrator,
                DemoRoleNames.BranchManager,
                DemoRoleNames.SalesRepresentative))
            .AddPolicy(ReadSales, policy => policy.RequireRole(DemoRoleNames.All))
            .AddPolicy(ManageSales, policy => policy.RequireRole(
                DemoRoleNames.SystemAdministrator,
                DemoRoleNames.BranchManager,
                DemoRoleNames.SalesRepresentative))
            .AddPolicy(ReadWorkOrders, policy => policy.RequireRole(DemoRoleNames.All))
            .AddPolicy(ManageWorkOrders, policy => policy.RequireRole(
                DemoRoleNames.SystemAdministrator,
                DemoRoleNames.BranchManager))
            .AddPolicy(UpdateWorkOrders, policy => policy.RequireRole(
                DemoRoleNames.SystemAdministrator,
                DemoRoleNames.BranchManager,
                DemoRoleNames.FieldTechnician))
            .AddPolicy(ViewAudit, policy => policy.RequireRole(
                DemoRoleNames.SystemAdministrator,
                DemoRoleNames.BranchManager))
            .AddPolicy(ResetDemo, policy => policy.RequireRole(DemoRoleNames.SystemAdministrator));
        return services;
    }
}
