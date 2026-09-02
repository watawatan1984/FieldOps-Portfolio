using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FieldOps.Web.Controllers;

[AllowAnonymous]
[Route("manual")]
public sealed class ManualController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();
}