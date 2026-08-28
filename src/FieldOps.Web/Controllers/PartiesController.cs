using System.Security.Claims;

using FieldOps.Domain.Enums;
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
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

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
    public async Task<IActionResult> Create(
        [FromQuery] Guid branchId,
        [FromQuery] PartyRoleType? role,
        CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizeBranchAsync(branchId, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        PartyRoleType? initialRole = role is PartyRoleType candidate && Enum.IsDefined(candidate)
            ? candidate
            : null;
        await PopulateCreateContextAsync(branchId, initialRole, cancellationToken);
        return View("Create", new CreatePartyInput { BranchId = branchId, RoleType = initialRole });
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
            ModelState.AddModelError(nameof(input.ContactLastName), "担当者の姓と名を両方入力してください");
        }

        if (input.RoleType is null || !Enum.IsDefined(input.RoleType.Value))
        {
            ModelState.Remove(nameof(input.RoleType));
            AddPartyError(nameof(input.RoleType), "顧客または協力会社を選んでください。");
        }

        await PopulateCreateContextAsync(input.BranchId, input.RoleType, cancellationToken);
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
            _ = exception;
            AddPartyError(nameof(input.OrganizationName), "同じ組織名がすでに登録されています");
            await PopulateCreateContextAsync(input.BranchId, input.RoleType, cancellationToken);
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
            AddPartyError(string.Empty, "顧客または協力会社を選んでください");
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
            _ = exception;
            AddPartyError(nameof(input.OrganizationName), "同じ組織名がすでに登録されています");
            Response.StatusCode = StatusCodes.Status409Conflict;
            return View(input);
        }
        catch (PartyConcurrencyException)
        {
            AddPartyError(string.Empty, "ほかの利用者が先に更新しました。画面を読み込み直して、もう一度操作してください");
            Response.StatusCode = StatusCodes.Status409Conflict;
            return View(input);
        }
        catch (PartyRoleRemovalException exception)
        {
            _ = exception;
            AddPartyError(string.Empty, "すでに登録済みの区分はこの画面では外せません");
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
            if (input.TargetBranchId is null || input.TargetBranchId == Guid.Empty)
            {
                AddPartyError(nameof(input.TargetBranchId), "共有先の支店を選んでください");
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
            ModelState.Remove(nameof(input.Version));
            AddPartyError(string.Empty, "ほかの利用者が先に更新しました。画面を読み込み直して、もう一度操作してください");
            return await EditWithShareOutcomeAsync(
                id,
                input.BranchId,
                StatusCodes.Status409Conflict,
                cancellationToken);
        }
        catch (PartyAlreadySharedException exception)
        {
            _ = exception;
            AddPartyError(nameof(input.TargetBranchId), "この支店にはすでに共有されています");
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

    private async Task PopulateCreateContextAsync(
        Guid branchId,
        PartyRoleType? role,
        CancellationToken cancellationToken)
    {
        ViewData["BranchName"] = await queries.GetBranchNameAsync(branchId, cancellationToken);
        string target = role switch
        {
            PartyRoleType.Customer => "顧客",
            PartyRoleType.BusinessPartner => "協力会社",
            _ => "顧客・協力会社"
        };
        ViewData["CreateTitle"] = $"{target}を登録";
        ViewData["CreateHeading"] = $"{target}を登録する";
        ViewData["CreateSubmit"] = role switch
        {
            PartyRoleType.Customer => "この内容で顧客を登録する",
            PartyRoleType.BusinessPartner => "この内容で協力会社を登録する",
            _ => "この内容で登録する"
        };
    }

    private void AddPartyError(string key, string message)
    {
        ModelState.AddModelError(key, message);
        ViewData["PartyError"] = message;
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
