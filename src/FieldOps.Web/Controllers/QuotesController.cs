using System.Security.Claims;

using FieldOps.Domain.Common;
using FieldOps.Domain.Enums;
using FieldOps.Features.Quotes;
using FieldOps.Features.Sales;
using FieldOps.Infrastructure.Identity;
using FieldOps.Web.Authorization;
using FieldOps.Web.Documents;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using QuestPDF.Fluent;

namespace FieldOps.Web.Controllers;

[Authorize(Policy = Policies.ReadSales)]
[Route("quotes")]
public sealed class QuotesController(
    QuoteQueries queries,
    QuoteCommands commands,
    SalesQueries salesQueries,
    IFieldOpsResourceAuthorizer resourceAuthorizer,
    IAuthorizationService authorizationService) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] QuoteSearchRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(InputErrorMessage);
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
                request.ValidUntilFrom,
                request.ValidUntilTo,
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
        catch (QuotePageOutOfRangeException exception)
        {
            _ = exception;
            return BadRequest(PageErrorMessage);
        }
    }

    [HttpGet("create")]
    [Authorize(Policy = Policies.ManageSales)]
    public async Task<IActionResult> Create([FromQuery] Guid branchId, CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizeBranchAsync(branchId, BranchResourceAction.ManageSales, cancellationToken);
        if (denied is not null) return denied;
        QuoteEditInput input = new() { BranchId = branchId, LineItems = [new QuoteLineItemInput()] };
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
    public async Task<IActionResult> Create(QuoteEditInput input, CancellationToken cancellationToken)
    {
        IActionResult? branchDenied = await AuthorizeBranchAsync(input.BranchId, BranchResourceAction.ManageSales, cancellationToken);
        if (branchDenied is not null) return branchDenied;
        IActionResult? opportunityDenied = await AuthorizeOpportunityAsync(input.SalesOpportunityId, BranchResourceAction.ManageSales, cancellationToken);
        if (opportunityDenied is not null) return opportunityDenied;
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
            ModelState.AddModelError(string.Empty, ForQuoteDisplayError(exception.Message));
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("Edit", input);
        }
        catch (UnauthorizedAccessException exception)
        {
            ModelState.AddModelError(string.Empty, ForQuoteDisplayError(exception.Message));
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("Edit", input);
        }
    }

    [HttpGet("from-opportunity/{salesOpportunityId:guid}")]
    [Authorize(Policy = Policies.ManageSales)]
    public async Task<IActionResult> CreateFromOpportunity(Guid salesOpportunityId, CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizeOpportunityAsync(salesOpportunityId, BranchResourceAction.ManageSales, cancellationToken);
        if (denied is not null) return denied;

        SalesEditInput? opportunity = await salesQueries.GetEditAsync(salesOpportunityId, cancellationToken);
        if (opportunity is null) return NotFound();

        QuoteEditInput input = new()
        {
            BranchId = opportunity.BranchId,
            SalesOpportunityId = salesOpportunityId,
            LineItems = [new QuoteLineItemInput()]
        };
        if (User.IsInRole(DemoRoleNames.SalesRepresentative))
        {
            input.OwnerUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        }
        await PopulateEditorAsync(opportunity.BranchId, true, cancellationToken);
        return View("Edit", input);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        QuoteEditInput? loaded = await queries.GetEditAsync(id, cancellationToken);
        if (loaded is null) return NotFound();
        IActionResult? denied = await AuthorizeOpportunityAsync(loaded.SalesOpportunityId, BranchResourceAction.ReadSales, cancellationToken);
        if (denied is not null) return denied;
        bool canManage = await resourceAuthorizer.AuthorizeSalesOpportunityAsync(
            User, loaded.SalesOpportunityId, BranchResourceAction.ManageSales, cancellationToken) == ResourceAuthorizationOutcome.Allowed;
        bool canViewAudit = (await authorizationService.AuthorizeAsync(User, Policies.ViewAudit)).Succeeded &&
            await resourceAuthorizer.AuthorizeSalesOpportunityAsync(
                User, loaded.SalesOpportunityId, BranchResourceAction.ViewAudit, cancellationToken) == ResourceAuthorizationOutcome.Allowed;
        QuoteDetailsViewModel? details = await queries.GetDetailsAsync(id, canManage, canViewAudit, cancellationToken);
        return details is null ? NotFound() : View(details);
    }

    [HttpGet("{id:guid}/pdf")]
    public async Task<IActionResult> Pdf(Guid id, CancellationToken cancellationToken)
    {
        QuoteDetailsViewModel? details = await queries.GetDetailsAsync(id, false, false, cancellationToken);
        if (details is null) return NotFound();
        IActionResult? denied = await AuthorizeOpportunityAsync(details.SalesOpportunityId, BranchResourceAction.ReadSales, cancellationToken);
        if (denied is not null) return denied;

        byte[] pdfBytes = new QuoteDocument(details).GeneratePdf();
        string fileDownloadName = $"見積書_{details.QuoteNumber}_第{details.RevisionNumber}版.pdf";
        return File(pdfBytes, "application/pdf", fileDownloadName);
    }

    [HttpGet("{id:guid}/edit")]
    [Authorize(Policy = Policies.ManageSales)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        QuoteDetailsViewModel? current = await queries.GetDetailsAsync(id, false, false, cancellationToken);
        if (current is null) return NotFound();
        IActionResult? denied = await AuthorizeOpportunityAsync(current.SalesOpportunityId, BranchResourceAction.ManageSales, cancellationToken);
        if (denied is not null) return denied;
        if (current.Status != QuoteStatus.Draft)
        {
            return RedirectToAction(nameof(Details), new { id });
        }
        QuoteEditInput? input = await queries.GetEditAsync(id, cancellationToken);
        if (input is null) return NotFound();
        await PopulateEditorAsync(input.BranchId, false, cancellationToken);
        ViewData["OpportunitySummary"] = $"{current.PartyName} - {current.SiteName}";
        return View(input);
    }

    [HttpPost("{id:guid}/edit")]
    [Authorize(Policy = Policies.ManageSales)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, QuoteEditInput input, CancellationToken cancellationToken)
    {
        if (id != input.Id) return BadRequest(InputErrorMessage);
        QuoteEditInput? loaded = await queries.GetEditAsync(id, cancellationToken);
        if (loaded is null) return NotFound();
        IActionResult? denied = await AuthorizeOpportunityAsync(loaded.SalesOpportunityId, BranchResourceAction.ManageSales, cancellationToken);
        if (denied is not null) return denied;
        if (input.BranchId != loaded.BranchId || input.SalesOpportunityId != loaded.SalesOpportunityId)
        {
            return BadRequest(InputErrorMessage);
        }
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
        catch (QuoteConcurrencyException)
        {
            IActionResult? currentDenied = await AuthorizeOpportunityAsync(loaded.SalesOpportunityId, BranchResourceAction.ManageSales, cancellationToken);
            if (currentDenied is not null) return currentDenied;
            QuoteEditInput? latest = await queries.GetEditAsync(id, cancellationToken);
            if (latest is null) return NotFound();
            ModelState.Remove(nameof(input.Version));
            input.Version = latest.Version;
            ModelState.AddModelError(string.Empty, "ほかの利用者が先に更新しました。最新の内容を確認してください。");
            Response.StatusCode = StatusCodes.Status409Conflict;
            return View(input);
        }
        catch (DomainException exception)
        {
            ModelState.AddModelError(string.Empty, ForQuoteDisplayError(exception.Message));
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View(input);
        }
        catch (UnauthorizedAccessException exception)
        {
            ModelState.AddModelError(string.Empty, ForQuoteDisplayError(exception.Message));
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View(input);
        }
    }

    [HttpPost("{id:guid}/transition")]
    [Authorize(Policy = Policies.ManageSales)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Transition(Guid id, QuoteTransitionInput input, CancellationToken cancellationToken)
    {
        QuoteEditInput? loaded = await queries.GetEditAsync(id, cancellationToken);
        if (loaded is null) return NotFound();
        IActionResult? denied = await AuthorizeOpportunityAsync(loaded.SalesOpportunityId, BranchResourceAction.ManageSales, cancellationToken);
        if (denied is not null) return denied;
        if (!ModelState.IsValid) return BadRequest(InputErrorMessage);
        try
        {
            await commands.TransitionAsync(id, input, cancellationToken);
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (QuoteConcurrencyException)
        {
            ModelState.AddModelError(string.Empty, "ほかの利用者が先に更新しました。最新の内容を確認してください。");
            return await DetailsWithOutcomeAsync(id, loaded.SalesOpportunityId, StatusCodes.Status409Conflict, cancellationToken);
        }
        catch (DomainException exception)
        {
            ModelState.AddModelError(string.Empty, ForQuoteDisplayError(exception.Message));
            return await DetailsWithOutcomeAsync(id, loaded.SalesOpportunityId, StatusCodes.Status400BadRequest, cancellationToken);
        }
        catch (UnauthorizedAccessException exception)
        {
            ModelState.AddModelError(string.Empty, ForQuoteDisplayError(exception.Message));
            return await DetailsWithOutcomeAsync(id, loaded.SalesOpportunityId, StatusCodes.Status400BadRequest, cancellationToken);
        }
    }

    private async Task<IActionResult?> AuthorizeBranchAsync(Guid branchId, BranchResourceAction action, CancellationToken cancellationToken) =>
        await resourceAuthorizer.AuthorizeBranchAsync(User, branchId, action, cancellationToken) switch
        {
            ResourceAuthorizationOutcome.Allowed => null,
            ResourceAuthorizationOutcome.NotFound => NotFound(),
            _ => Forbid()
        };

    private async Task<IActionResult?> AuthorizeOpportunityAsync(Guid salesOpportunityId, BranchResourceAction action, CancellationToken cancellationToken) =>
        await resourceAuthorizer.AuthorizeSalesOpportunityAsync(User, salesOpportunityId, action, cancellationToken) switch
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

    private static string ForQuoteDisplayError(string message)
    {
        if (message.StartsWith("Quote transition from ", StringComparison.Ordinal))
        {
            if (message.Contains("at least one line item", StringComparison.Ordinal))
            {
                return "発行するには明細を1行以上入力してください。";
            }
            if (message.Contains("validity date that is not in the past", StringComparison.Ordinal))
            {
                return "有効期限が過去の日付になっています。";
            }
            if (message.Contains("validity date", StringComparison.Ordinal))
            {
                return "発行するには有効期限を入力してください。";
            }
            return "この状態には変更できません。最新の状態を確認してください。";
        }

        if (message.StartsWith("A quote in ", StringComparison.Ordinal))
        {
            return "下書き以外の見積は編集できません。";
        }

        return message switch
        {
            "A quote validity date is required." => "有効期限を入力してください。",
            "A quote requires at least one line item." => "明細を1行以上入力してください。",
            "A quote line item quantity must be greater than zero." => "数量は0より大きい値で入力してください。",
            "A quote line item unit price must not be negative." => "単価は0円以上で入力してください。",
            "A quote tax rate must be between 0 and 100 percent." => "消費税率は0〜100%の範囲で入力してください。",
            "Select a sales owner in this branch." => "この支店の営業担当者を選んでください。",
            "A quote sales opportunity cannot be changed." => "見積の営業案件は変更できません。",
            "A quote must belong to the branch of its sales opportunity." => "見積の支店が営業案件と一致しません。",
            "Sales representatives can manage only their own quotes." => "自分が担当する見積のみ操作できます。",
            _ => "見積を更新できませんでした。入力内容を確認してください。"
        };
    }

    private async Task<IActionResult> DetailsWithOutcomeAsync(
        Guid id,
        Guid salesOpportunityId,
        int statusCode,
        CancellationToken cancellationToken)
    {
        ResourceAuthorizationOutcome readOutcome = await resourceAuthorizer.AuthorizeSalesOpportunityAsync(
            User,
            salesOpportunityId,
            BranchResourceAction.ReadSales,
            cancellationToken);
        if (readOutcome is not ResourceAuthorizationOutcome.Allowed)
        {
            return readOutcome is ResourceAuthorizationOutcome.NotFound ? NotFound() : Forbid();
        }

        bool canManage = await resourceAuthorizer.AuthorizeSalesOpportunityAsync(
            User,
            salesOpportunityId,
            BranchResourceAction.ManageSales,
            cancellationToken) == ResourceAuthorizationOutcome.Allowed;
        if (!canManage)
        {
            return Forbid();
        }

        bool canViewAudit = (await authorizationService.AuthorizeAsync(User, Policies.ViewAudit)).Succeeded &&
            await resourceAuthorizer.AuthorizeSalesOpportunityAsync(
                User,
                salesOpportunityId,
                BranchResourceAction.ViewAudit,
                cancellationToken) == ResourceAuthorizationOutcome.Allowed;
        QuoteDetailsViewModel? details = await queries.GetDetailsAsync(
            id,
            canManage,
            canViewAudit,
            cancellationToken);
        if (details is null) return NotFound();
        Response.StatusCode = statusCode;
        return View("Details", details);
    }

    private const string InputErrorMessage = "入力内容が正しくありません。";

    private const string PageErrorMessage = "ページ番号が正しくありません。";
}