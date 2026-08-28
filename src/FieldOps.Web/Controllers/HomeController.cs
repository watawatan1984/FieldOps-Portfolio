using System.Diagnostics;
using System.Security.Claims;

using FieldOps.Features.Dashboard;
using FieldOps.Infrastructure.Identity;
using FieldOps.Web.Authorization;
using FieldOps.Web.Models;
using FieldOps.Web.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FieldOps.Web.Controllers;

[Authorize(Policy = Policies.ViewDashboard)]
public sealed class HomeController(
    DashboardQueries dashboardQueries,
    DashboardPageModelFactory dashboardPageModelFactory) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        Guid? branchId = null;
        if (!User.IsInRole(DemoRoleNames.SystemAdministrator))
        {
            if (!Guid.TryParse(
                User.FindFirstValue(DemoUserClaimsPrincipalFactory.BranchIdClaimType),
                out Guid claimedBranchId))
            {
                return Forbid();
            }

            branchId = claimedBranchId;
        }

        DashboardMetrics metrics = await dashboardQueries.GetAsync(branchId, cancellationToken);
        string role = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
        return View(dashboardPageModelFactory.Create(metrics, role, branchId));
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
