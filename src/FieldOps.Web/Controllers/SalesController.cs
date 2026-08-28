using System.Security.Claims;

using FieldOps.Domain.Common;
using FieldOps.Features.Abstractions;
using FieldOps.Features.Sales;
using FieldOps.Infrastructure.Identity;
using FieldOps.Web.Authorization;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FieldOps.Web.Controllers;

[Authorize(Policy = Policies.ReadSales)]
[Route("sales")]
public sealed class SalesController(
    SalesQueries queries,
    SalesCommands commands,
    IFieldOpsResourceAuthorizer resourceAuthorizer,
    IAuthorizationService authorizationService) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] SalesSearchRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (request.BranchId == Guid.Empty && !User.IsInRole(DemoRoleNames.SystemAdministrator))
        {
            Guid branchId = Guid.TryParse(User.FindFirstValue(DemoUserClaimsPrincipalFactory.BranchIdClaimType), out Guid claimedBranchId)
                ? claimedBranchId
                : await queries.GetDefaultBranchIdAsync(cancellationToken);
            return RedirectToAction(nameof(Index), new
            {
                branchId,
                request.OwnerUserId,
                request.Status,
                request.ExpectedCloseFrom,
                request.ExpectedCloseTo,
                request.MinimumAmount,
                request.MaximumAmount,
                request.Search,
                request.Page,
                request.PageSize
            });
        }

        if (request.BranchId != Guid.Empty)
        {
            IActionResult? denied = await AuthorizeBranchAsync(request.BranchId, BranchResourceAction.ReadSales, cancellationToken);
            if (denied is not null) return denied;
        }
        try
        {
            return View(await queries.SearchAsync(request, cancellationToken));
        }
        catch (SalesPageOutOfRangeException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    [HttpGet("create")]
    [Authorize(Policy = Policies.ManageSales)]
    public async Task<IActionResult> Create([FromQuery] Guid branchId, CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizeBranchAsync(branchId, BranchResourceAction.ManageSales, cancellationToken);
        if (denied is not null) return denied;
        SalesEditInput input = new() { BranchId = branchId };
        if (User.IsInRole(DemoRoleNames.SalesRepresentative))
        {
            input.OwnerUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        }
        await PopulateEditorAsync(branchId, true, cancellationToken);
        return View("Edit", input);
    }

    [HttpPost("create")]
    [Authorize(Policy = Policies.ManageSales)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(SalesEditInput input, CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizeBranchAsync(input.BranchId, BranchResourceAction.ManageSales, cancellationToken);
        if (denied is not null) return denied;
        ValidateProposalPair(input);
        await PopulateEditorAsync(input.BranchId, true, cancellationToken);
        if (!ModelState.IsValid)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("Edit", input);
        }
        try
        {
            Guid id = await commands.CreateAsync(input, cancellationToken);
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (DomainException exception)
        {
            ModelState.AddModelError(string.Empty, ForSalesDisplayError(exception.Message));
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("Edit", input);
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizeOpportunityAsync(id, BranchResourceAction.ReadSales, cancellationToken);
        if (denied is not null) return denied;
        bool canManage = await resourceAuthorizer.AuthorizeSalesOpportunityAsync(
            User, id, BranchResourceAction.ManageSales, cancellationToken) == ResourceAuthorizationOutcome.Allowed;
        bool canViewAudit = (await authorizationService.AuthorizeAsync(User, Policies.ViewAudit)).Succeeded &&
            await resourceAuthorizer.AuthorizeSalesOpportunityAsync(
                User, id, BranchResourceAction.ViewAudit, cancellationToken) == ResourceAuthorizationOutcome.Allowed;
        SalesDetailsViewModel? details = await queries.GetDetailsAsync(id, canManage, canViewAudit, cancellationToken);
        return details is null ? NotFound() : View(details);
    }

    [HttpGet("{id:guid}/edit")]
    [Authorize(Policy = Policies.ManageSales)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizeOpportunityAsync(id, BranchResourceAction.ManageSales, cancellationToken);
        if (denied is not null) return denied;
        SalesEditInput? input = await queries.GetEditAsync(id, cancellationToken);
        if (input is null) return NotFound();
        await PopulateEditorAsync(input.BranchId, false, cancellationToken);
        return View(input);
    }

    [HttpPost("{id:guid}/edit")]
    [Authorize(Policy = Policies.ManageSales)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, SalesEditInput input, CancellationToken cancellationToken)
    {
        if (id != input.Id) return BadRequest();
        IActionResult? denied = await AuthorizeOpportunityAsync(id, BranchResourceAction.ManageSales, cancellationToken);
        if (denied is not null) return denied;
        SalesEditInput? loaded = await queries.GetEditAsync(id, cancellationToken);
        if (loaded is null) return NotFound();
        if (input.BranchId != loaded.BranchId || input.PartyId != loaded.PartyId || input.SiteId != loaded.SiteId)
        {
            return BadRequest();
        }
        ValidateProposalPair(input);
        await PopulateEditorAsync(loaded.BranchId, false, cancellationToken);
        if (!ModelState.IsValid)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View(input);
        }
        try
        {
            await commands.UpdateAsync(input, cancellationToken);
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (SalesConcurrencyException)
        {
            IActionResult? currentDenied = await AuthorizeOpportunityAsync(
                id,
                BranchResourceAction.ManageSales,
                cancellationToken);
            if (currentDenied is not null) return currentDenied;
            SalesEditInput? latest = await queries.GetEditAsync(id, cancellationToken);
            if (latest is null) return NotFound();
            ModelState.Remove(nameof(input.Version));
            input.Version = latest.Version;
            ModelState.AddModelError(string.Empty, "ほかの利用者が先に更新しました。最新の内容を確認してください。");
            Response.StatusCode = StatusCodes.Status409Conflict;
            return View(input);
        }
        catch (DomainException exception)
        {
            ModelState.AddModelError(string.Empty, ForSalesDisplayError(exception.Message));
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View(input);
        }
    }

    [HttpPost("{id:guid}/transition")]
    [Authorize(Policy = Policies.ManageSales)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Transition(Guid id, SalesTransitionInput input, CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizeOpportunityAsync(id, BranchResourceAction.ManageSales, cancellationToken);
        if (denied is not null) return denied;
        if (!ModelState.IsValid) return BadRequest(ModelState);
        try
        {
            await commands.TransitionAsync(id, input, cancellationToken);
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (SalesConcurrencyException)
        {
            ModelState.AddModelError(string.Empty, "ほかの利用者が先に更新しました。最新の内容を確認してください。");
            return await DetailsWithOutcomeAsync(id, StatusCodes.Status409Conflict, cancellationToken);
        }
        catch (DomainException exception)
        {
            ModelState.AddModelError(string.Empty, ForSalesDisplayError(exception.Message));
            return await DetailsWithOutcomeAsync(id, StatusCodes.Status400BadRequest, cancellationToken);
        }
    }

    private async Task<IActionResult?> AuthorizeBranchAsync(Guid branchId, BranchResourceAction action, CancellationToken cancellationToken) =>
        await resourceAuthorizer.AuthorizeBranchAsync(User, branchId, action, cancellationToken) switch
        {
            ResourceAuthorizationOutcome.Allowed => null,
            ResourceAuthorizationOutcome.NotFound => NotFound(),
            _ => Forbid()
        };

    private async Task<IActionResult?> AuthorizeOpportunityAsync(Guid id, BranchResourceAction action, CancellationToken cancellationToken) =>
        await resourceAuthorizer.AuthorizeSalesOpportunityAsync(User, id, action, cancellationToken) switch
        {
            ResourceAuthorizationOutcome.Allowed => null,
            ResourceAuthorizationOutcome.NotFound => NotFound(),
            _ => Forbid()
        };

    private async Task PopulateEditorAsync(Guid branchId, bool isCreate, CancellationToken cancellationToken)
    {
        ViewData["EditorOptions"] = await queries.GetEditorOptionsAsync(branchId, cancellationToken);
        ViewData["IsCreate"] = isCreate;
    }

    private void ValidateProposalPair(SalesEditInput input)
    {
        if (input.ProposedAmount.HasValue != input.ExpectedCloseDate.HasValue)
        {
            ModelState.AddModelError(string.Empty, "提案金額と予定日は両方入力してください。");
        }
    }

    private static string ForSalesDisplayError(string message)
    {
        if (message.StartsWith("SalesOpportunity transition from ", StringComparison.Ordinal))
        {
            return "この状態には変更できません。最新の状態を確認してください。";
        }

        return message switch
        {
            "Proposal amount and expected close date must be provided together." => "提案金額と予定日は両方入力してください。",
            "An existing sales opportunity proposal cannot be cleared." => "登録済みの提案金額と予定日は空にできません。",
            "A sales opportunity proposal amount must be greater than zero." => "提案金額は1円以上で入力してください。",
            "A sales opportunity expected close date is required." => "予定日を入力してください。",
            "Select a sales owner in this branch." => "この支店の営業担当者を選んでください。",
            "Select a technician in this branch." => "この支店の現場担当者を選んでください。",
            _ => "営業案件を更新できませんでした。入力内容を確認してください。"
        };
    }

    private async Task<IActionResult> DetailsWithOutcomeAsync(Guid id, int statusCode, CancellationToken cancellationToken)
    {
        ResourceAuthorizationOutcome readOutcome = await resourceAuthorizer.AuthorizeSalesOpportunityAsync(
            User,
            id,
            BranchResourceAction.ReadSales,
            cancellationToken);
        if (readOutcome is not ResourceAuthorizationOutcome.Allowed)
        {
            return readOutcome is ResourceAuthorizationOutcome.NotFound ? NotFound() : Forbid();
        }

        bool canManage = await resourceAuthorizer.AuthorizeSalesOpportunityAsync(
            User,
            id,
            BranchResourceAction.ManageSales,
            cancellationToken) == ResourceAuthorizationOutcome.Allowed;
        if (!canManage)
        {
            return Forbid();
        }

        bool canViewAudit = (await authorizationService.AuthorizeAsync(User, Policies.ViewAudit)).Succeeded &&
            await resourceAuthorizer.AuthorizeSalesOpportunityAsync(
                User,
                id,
                BranchResourceAction.ViewAudit,
                cancellationToken) == ResourceAuthorizationOutcome.Allowed;
        SalesDetailsViewModel? details = await queries.GetDetailsAsync(
            id,
            canManage,
            canViewAudit,
            cancellationToken);
        if (details is null) return NotFound();
        Response.StatusCode = statusCode;
        return View("Details", details);
    }
}
