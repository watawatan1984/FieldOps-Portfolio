using System.Security.Claims;

using FieldOps.Features.Parties;
using FieldOps.Infrastructure.Identity;
using FieldOps.Web.Authorization;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FieldOps.Web.Controllers;

[Authorize(Policy = Policies.ManageParties)]
[Route("parties")]
public sealed class PartiesController(
    PartyQueries queries,
    PartyCommands commands,
    IFieldOpsResourceAuthorizer resourceAuthorizer) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(
        [FromQuery] PartySearchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.BranchId == Guid.Empty)
        {
            Guid branchId = Guid.TryParse(
                User.FindFirstValue(DemoUserClaimsPrincipalFactory.BranchIdClaimType),
                out Guid claimedBranchId)
                    ? claimedBranchId
                    : await queries.GetDefaultBranchIdAsync(cancellationToken);
            return RedirectToAction(nameof(Index), new
            {
                branchId,
                request.Search,
                request.Role,
                request.Page,
                request.PageSize
            });
        }

        IActionResult? denied = await AuthorizeBranchAsync(request.BranchId, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        try
        {
            return View(await queries.SearchAsync(request, cancellationToken));
        }
        catch (PartyPageOutOfRangeException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    [HttpGet("create")]
    public async Task<IActionResult> Create([FromQuery] Guid branchId, CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizeBranchAsync(branchId, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        ViewData["BranchName"] = await queries.GetBranchNameAsync(branchId, cancellationToken);
        return View("Create", new CreatePartyInput { BranchId = branchId });
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreatePartyInput input, CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizeBranchAsync(input.BranchId, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        if (string.IsNullOrWhiteSpace(input.ContactFirstName) != string.IsNullOrWhiteSpace(input.ContactLastName))
        {
            ModelState.AddModelError(nameof(input.ContactLastName), "Enter both contact first and last names.");
        }

        ViewData["BranchName"] = await queries.GetBranchNameAsync(input.BranchId, cancellationToken);
        if (!ModelState.IsValid)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("Create", input);
        }

        try
        {
            Guid id = await commands.CreateAsync(input, cancellationToken);
            return RedirectToAction(nameof(Details), new { id, branchId = input.BranchId });
        }
        catch (PartyDuplicateException exception)
        {
            ModelState.AddModelError(nameof(input.OrganizationName), exception.Message);
            Response.StatusCode = StatusCodes.Status409Conflict;
            return View("Create", input);
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, [FromQuery] Guid branchId, CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizePartyAsync(id, branchId, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        PartyDetailsViewModel? party = await queries.GetDetailsAsync(
            id,
            branchId,
            User.IsInRole(DemoRoleNames.SystemAdministrator),
            cancellationToken);
        return party is null ? NotFound() : View(party);
    }

    [HttpGet("{id:guid}/edit")]
    public async Task<IActionResult> Edit(Guid id, [FromQuery] Guid branchId, CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizePartyAsync(id, branchId, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        EditPartyInput? input = await queries.GetEditAsync(id, branchId, cancellationToken);
        if (input is null)
        {
            return NotFound();
        }

        await PopulateEditContextAsync(branchId, cancellationToken);
        return View(input);
    }

    [HttpPost("{id:guid}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        Guid id,
        EditPartyInput input,
        CancellationToken cancellationToken)
    {
        if (id != input.Id)
        {
            return BadRequest();
        }

        IActionResult? denied = await AuthorizePartyAsync(id, input.BranchId, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        if (!input.IsCustomer && !input.IsBusinessPartner)
        {
            ModelState.AddModelError(string.Empty, "Select at least one party role.");
        }

        await PopulateEditContextAsync(input.BranchId, cancellationToken);
        if (!ModelState.IsValid)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View(input);
        }

        try
        {
            await commands.UpdateAsync(input, cancellationToken);
            return RedirectToAction(nameof(Details), new { id, branchId = input.BranchId });
        }
        catch (PartyDuplicateException exception)
        {
            ModelState.AddModelError(nameof(input.OrganizationName), exception.Message);
            Response.StatusCode = StatusCodes.Status409Conflict;
            return View(input);
        }
        catch (PartyConcurrencyException)
        {
            ModelState.AddModelError(string.Empty, "This party changed after you opened the form. Reload and try again.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            return View(input);
        }
        catch (PartyRoleRemovalException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View(input);
        }
    }

    [HttpPost("{id:guid}/share")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Share(
        Guid id,
        SharePartyInput input,
        CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizePartyAsync(id, input.BranchId, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        if (!ModelState.IsValid || input.TargetBranchId is null || input.TargetBranchId == Guid.Empty)
        {
            if (input.TargetBranchId == Guid.Empty)
            {
                ModelState.AddModelError(nameof(input.TargetBranchId), "Select a target branch.");
            }

            return await EditWithShareOutcomeAsync(
                id,
                input.BranchId,
                StatusCodes.Status400BadRequest,
                cancellationToken);
        }

        IActionResult? targetDenied = await AuthorizeBranchAsync(input.TargetBranchId.Value, cancellationToken);
        if (targetDenied is not null)
        {
            return targetDenied;
        }

        try
        {
            await commands.ShareAsync(id, input, cancellationToken);
            return RedirectToAction(nameof(Details), new { id, branchId = input.BranchId });
        }
        catch (PartyConcurrencyException)
        {
            ModelState.AddModelError(string.Empty, "This party changed after you opened the form. Reload and try again.");
            return await EditWithShareOutcomeAsync(
                id,
                input.BranchId,
                StatusCodes.Status409Conflict,
                cancellationToken);
        }
        catch (PartyAlreadySharedException exception)
        {
            ModelState.AddModelError(nameof(input.TargetBranchId), exception.Message);
            return await EditWithShareOutcomeAsync(
                id,
                input.BranchId,
                StatusCodes.Status409Conflict,
                cancellationToken);
        }
    }

    private async Task<IActionResult?> AuthorizeBranchAsync(Guid branchId, CancellationToken cancellationToken) =>
        await resourceAuthorizer.AuthorizeBranchAsync(
            User,
            branchId,
            BranchResourceAction.ManageParties,
            cancellationToken) switch
        {
            ResourceAuthorizationOutcome.Allowed => null,
            ResourceAuthorizationOutcome.NotFound => NotFound(),
            _ => Forbid()
        };

    private async Task<IActionResult?> AuthorizePartyAsync(Guid partyId, Guid branchId, CancellationToken cancellationToken) =>
        await resourceAuthorizer.AuthorizePartyAsync(
            User,
            partyId,
            branchId,
            BranchResourceAction.ManageParties,
            cancellationToken) switch
        {
            ResourceAuthorizationOutcome.Allowed => null,
            ResourceAuthorizationOutcome.NotFound => NotFound(),
            _ => Forbid()
        };

    private async Task PopulateEditContextAsync(Guid branchId, CancellationToken cancellationToken)
    {
        ViewData["BranchName"] = await queries.GetBranchNameAsync(branchId, cancellationToken);
        ViewData["Branches"] = User.IsInRole(DemoRoleNames.SystemAdministrator)
            ? await queries.GetBranchOptionsAsync(cancellationToken)
            : Array.Empty<BranchOption>();
    }

    private async Task<IActionResult> EditWithShareOutcomeAsync(
        Guid partyId,
        Guid branchId,
        int statusCode,
        CancellationToken cancellationToken)
    {
        EditPartyInput? editInput = await queries.GetEditAsync(partyId, branchId, cancellationToken);
        if (editInput is null)
        {
            return NotFound();
        }

        await PopulateEditContextAsync(branchId, cancellationToken);
        Response.StatusCode = statusCode;
        return View("Edit", editInput);
    }
}