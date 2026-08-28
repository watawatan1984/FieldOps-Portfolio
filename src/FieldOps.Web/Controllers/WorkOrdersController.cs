using System.Security.Claims;

using FieldOps.Domain.Common;
using FieldOps.Features.Work;
using FieldOps.Infrastructure.Identity;
using FieldOps.Web.Authorization;
using FieldOps.Web.Formatting;
using FieldOps.Web.Models;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FieldOps.Web.Controllers;

[Authorize(Policy = Policies.ReadWorkOrders)]
[Route("work-orders")]
public sealed class WorkOrdersController(
    WorkOrderQueries queries,
    WorkOrderCommands commands,
    IFieldOpsResourceAuthorizer resourceAuthorizer) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] WorkOrderSearchRequest request, CancellationToken cancellationToken)
    {
        if (request.BranchId == Guid.Empty && !User.IsInRole(DemoRoleNames.SystemAdministrator))
        {
            Guid branchId = Guid.TryParse(
                User.FindFirstValue(DemoUserClaimsPrincipalFactory.BranchIdClaimType),
                out Guid claimedBranchId)
                    ? claimedBranchId
                    : await queries.GetDefaultBranchIdAsync(cancellationToken);
            return RedirectToAction(nameof(Index), new { branchId, request.Page, request.PageSize });
        }
        if (request.BranchId != Guid.Empty)
        {
            IActionResult? denied = await AuthorizeBranchAsync(request.BranchId, BranchResourceAction.ReadWorkOrders, cancellationToken);
            if (denied is not null) return denied;
        }
        try
        {
            return View(await queries.SearchAsync(request, cancellationToken));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return BadRequest(LocalizeSafeError(exception));
        }
    }

    [HttpPost("from-opportunity/{salesOpportunityId:guid}")]
    [Authorize(Policy = Policies.ManageWorkOrders)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateFromOpportunity(
        Guid salesOpportunityId,
        CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizeOpportunityAsync(
            salesOpportunityId,
            BranchResourceAction.ManageWorkOrders,
            cancellationToken);
        if (denied is not null) return denied;

        try
        {
            Guid id = await commands.CreateFromOpportunityAsync(salesOpportunityId, cancellationToken);
            return Redirect($"/work-orders/{id}");
        }
        catch (WorkOrderAlreadyExistsException exception)
        {
            return Conflict(LocalizeSafeError(exception));
        }
        catch (DomainException exception)
        {
            return BadRequest(LocalizeSafeError(exception));
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizeWorkOrderAsync(id, BranchResourceAction.ReadWorkOrders, cancellationToken);
        if (denied is not null) return denied;
        bool canManage = await IsAuthorizedAsync(id, BranchResourceAction.ManageWorkOrders, cancellationToken);
        bool canUpdate = await IsAuthorizedAsync(id, BranchResourceAction.UpdateWorkOrders, cancellationToken);
        bool canCorrect = User.IsInRole(DemoRoleNames.SystemAdministrator);
        WorkOrderDetailsViewModel? model = await queries.GetDetailsAsync(
            id, canManage, canUpdate, canCorrect, cancellationToken);
        return model is null ? NotFound() : View(model);
    }

    [HttpGet("{id:guid}/edit")]
    [Authorize(Policy = Policies.ManageWorkOrders)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizeWorkOrderAsync(id, BranchResourceAction.ManageWorkOrders, cancellationToken);
        if (denied is not null) return denied;
        WorkOrderEditInput? input = await queries.GetEditAsync(id, cancellationToken);
        if (input is null) return NotFound();
        if (input.Status != FieldOps.Domain.Enums.WorkOrderStatus.Planned)
        {
            return RedirectToAction(nameof(Details), new { id });
        }
        ViewData["EditorOptions"] = await queries.GetEditorOptionsAsync(id, cancellationToken);
        return View(WorkOrderScheduleForm.FromCommand(input));
    }

    [HttpPost("{id:guid}/edit")]
    [Authorize(Policy = Policies.ManageWorkOrders)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit([FromRoute] Guid id, WorkOrderScheduleForm input, CancellationToken cancellationToken)
    {
        if (id != input.Id) return BadRequest("入力内容が正しくありません。");
        IActionResult? denied = await AuthorizeWorkOrderAsync(id, BranchResourceAction.ManageWorkOrders, cancellationToken);
        if (denied is not null) return denied;
        WorkOrderEditInput? current = await queries.GetEditAsync(id, cancellationToken);
        if (current is null) return NotFound();
        if (current.Status != FieldOps.Domain.Enums.WorkOrderStatus.Planned)
        {
            return await DetailsWithOutcomeAsync(
                id,
                StatusCodes.Status409Conflict,
                cancellationToken,
                "この作業予定は既に日程が決まっているため、もう一度日程を保存できません。");
        }
        ViewData["EditorOptions"] = await queries.GetEditorOptionsAsync(id, cancellationToken);
        if (!ModelState.IsValid)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View(input);
        }
        try
        {
            await commands.ScheduleAndAssignAsync(input.ToCommand(), cancellationToken);
            return Redirect($"/work-orders/{id}");
        }
        catch (WorkOrderConcurrencyException)
        {
            IActionResult? currentDenied = await AuthorizeWorkOrderAsync(id, BranchResourceAction.ManageWorkOrders, cancellationToken);
            if (currentDenied is not null) return currentDenied;
            WorkOrderEditInput? latest = await queries.GetEditAsync(id, cancellationToken);
            if (latest is null) return NotFound();
            if (latest.Status != FieldOps.Domain.Enums.WorkOrderStatus.Planned)
            {
                return await DetailsWithOutcomeAsync(
                    id,
                    StatusCodes.Status409Conflict,
                    cancellationToken,
                    "この作業予定は既に日程が決まっているため、もう一度日程を保存できません。");
            }
            input.Version = latest.Version;
            input.Status = latest.Status;
            ModelState.Clear();
            ModelState.AddModelError(string.Empty, ConcurrencyMessage);
            Response.StatusCode = StatusCodes.Status409Conflict;
            return View(input);
        }
        catch (DomainException exception)
        {
            ModelState.AddModelError(string.Empty, LocalizeSafeError(exception));
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View(input);
        }
    }

    [HttpPost("{id:guid}/transition")]
    [Authorize(Policy = Policies.UpdateWorkOrders)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Transition(Guid id, WorkOrderTransitionInput input, CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizeWorkOrderAsync(id, BranchResourceAction.UpdateWorkOrders, cancellationToken);
        if (denied is not null) return denied;
        if (!ModelState.IsValid) return BadRequest(ModelState);
        try
        {
            await commands.TransitionAsync(id, input, cancellationToken);
            return Redirect($"/work-orders/{id}");
        }
        catch (WorkOrderConcurrencyException)
        {
            return await DetailsWithOutcomeAsync(id, StatusCodes.Status409Conflict, cancellationToken,
                ConcurrencyMessage);
        }
        catch (DomainException exception)
        {
            return await DetailsWithOutcomeAsync(id, StatusCodes.Status400BadRequest, cancellationToken, LocalizeSafeError(exception));
        }
    }

    [HttpGet("{id:guid}/events/add")]
    [Authorize(Policy = Policies.UpdateWorkOrders)]
    public async Task<IActionResult> AddEvent(Guid id, CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizeWorkOrderAsync(id, BranchResourceAction.UpdateWorkOrders, cancellationToken);
        if (denied is not null) return denied;
        IActionResult? eventDenied = await PopulateEventTypesAsync(id, cancellationToken);
        if (eventDenied is not null) return eventDenied;
        WorkOrderEditInput? workOrder = await queries.GetEditAsync(id, cancellationToken);
        if (workOrder is null) return NotFound();
        DateTime utcNow = DateTime.UtcNow;
        return View(new WorkEventForm
        {
            Version = workOrder.Version,
            OccurredDate = JapanTimeFormatter.ToJapanDate(utcNow),
            OccurredTime = JapanTimeFormatter.ToJapanTime(utcNow)
        });
    }

    [HttpPost("{id:guid}/events/add")]
    [Authorize(Policy = Policies.UpdateWorkOrders)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddEvent(Guid id, WorkEventForm input, CancellationToken cancellationToken)
    {
        IActionResult? denied = await AuthorizeWorkOrderAsync(id, BranchResourceAction.UpdateWorkOrders, cancellationToken);
        if (denied is not null) return denied;
        IActionResult? eventDenied = await PopulateEventTypesAsync(id, cancellationToken);
        if (eventDenied is not null) return eventDenied;
        if (!ModelState.IsValid)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View(input);
        }
        try
        {
            await commands.AddEventAsync(id, input.ToCommand(), cancellationToken);
            return Redirect($"/work-orders/{id}");
        }
        catch (WorkOrderConcurrencyException)
        {
            IActionResult? currentDenied = await AuthorizeWorkOrderAsync(id, BranchResourceAction.UpdateWorkOrders, cancellationToken);
            if (currentDenied is not null) return currentDenied;
            WorkOrderEditInput? latest = await queries.GetEditAsync(id, cancellationToken);
            if (latest is null) return NotFound();
            input.Version = latest.Version;
            eventDenied = await PopulateEventTypesAsync(id, cancellationToken);
            if (eventDenied is not null) return eventDenied;
            ModelState.Remove(nameof(input.Version));
            ModelState.AddModelError(string.Empty, ConcurrencyMessage);
            Response.StatusCode = StatusCodes.Status409Conflict;
            return View(input);
        }
        catch (DomainException exception)
        {
            ModelState.AddModelError(string.Empty, LocalizeSafeError(exception));
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View(input);
        }
    }

    private async Task<IActionResult?> AuthorizeOpportunityAsync(
        Guid id,
        BranchResourceAction action,
        CancellationToken cancellationToken) =>
        await resourceAuthorizer.AuthorizeSalesOpportunityAsync(User, id, action, cancellationToken) switch
        {
            ResourceAuthorizationOutcome.Allowed => null,
            ResourceAuthorizationOutcome.NotFound => NotFound(),
            _ => Forbid()
        };

    private async Task<IActionResult?> AuthorizeBranchAsync(
        Guid id,
        BranchResourceAction action,
        CancellationToken cancellationToken) =>
        await resourceAuthorizer.AuthorizeBranchAsync(User, id, action, cancellationToken) switch
        {
            ResourceAuthorizationOutcome.Allowed => null,
            ResourceAuthorizationOutcome.NotFound => NotFound(),
            _ => Forbid()
        };

    private async Task<IActionResult?> AuthorizeWorkOrderAsync(
        Guid id,
        BranchResourceAction action,
        CancellationToken cancellationToken) =>
        await resourceAuthorizer.AuthorizeWorkOrderAsync(User, id, action, cancellationToken) switch
        {
            ResourceAuthorizationOutcome.Allowed => null,
            ResourceAuthorizationOutcome.NotFound => NotFound(),
            _ => Forbid()
        };

    private async Task<bool> IsAuthorizedAsync(Guid id, BranchResourceAction action, CancellationToken cancellationToken) =>
        await resourceAuthorizer.AuthorizeWorkOrderAsync(User, id, action, cancellationToken) == ResourceAuthorizationOutcome.Allowed;

    private async Task<IActionResult> DetailsWithOutcomeAsync(
        Guid id,
        int statusCode,
        CancellationToken cancellationToken,
        string message)
    {
        IActionResult? denied = await AuthorizeWorkOrderAsync(id, BranchResourceAction.ReadWorkOrders, cancellationToken);
        if (denied is not null) return denied;
        if (!await IsAuthorizedAsync(id, BranchResourceAction.UpdateWorkOrders, cancellationToken)) return Forbid();
        ModelState.AddModelError(string.Empty, message);
        WorkOrderDetailsViewModel? model = await queries.GetDetailsAsync(
            id,
            await IsAuthorizedAsync(id, BranchResourceAction.ManageWorkOrders, cancellationToken),
            true,
            User.IsInRole(DemoRoleNames.SystemAdministrator),
            cancellationToken);
        if (model is null) return NotFound();
        Response.StatusCode = statusCode;
        return View("Details", model);
    }

    private async Task<IActionResult?> PopulateEventTypesAsync(Guid id, CancellationToken cancellationToken)
    {
        FieldOps.Domain.Enums.WorkOrderStatus? status = await queries.GetStatusAsync(id, cancellationToken);
        if (status is null) return NotFound();
        if (status == FieldOps.Domain.Enums.WorkOrderStatus.Completed && !User.IsInRole(DemoRoleNames.SystemAdministrator)) return Forbid();
        if (status == FieldOps.Domain.Enums.WorkOrderStatus.Cancelled) return BadRequest("取り消し済みの作業予定には記録を追加できません。");
        ViewData["EventTypes"] = status == FieldOps.Domain.Enums.WorkOrderStatus.Completed
            ? new[] { FieldOps.Domain.Enums.WorkEventType.Correction }
            : new[]
            {
                FieldOps.Domain.Enums.WorkEventType.Note,
                FieldOps.Domain.Enums.WorkEventType.Arrival,
                FieldOps.Domain.Enums.WorkEventType.Completion
            };
        return null;
    }

    private const string ConcurrencyMessage = "ほかの利用者が更新しました。最新の内容を確認して、もう一度実行してください。";

    private static string LocalizeSafeError(Exception exception) => exception switch
    {
        WorkOrderAlreadyExistsException => "この営業案件には既に作業予定があります。",
        ArgumentOutOfRangeException => "ページ番号が正しくありません。",
        DomainException domainException => LocalizeDomainError(domainException.Message),
        _ => "処理を完了できませんでした。入力内容を確認してください。"
    };

    private static string LocalizeDomainError(string message)
    {
        if (message.Contains("Select a technician in this branch.", StringComparison.Ordinal))
        {
            return "この支店の担当者を選んでください。";
        }
        if (message.Contains("A scheduled start is required.", StringComparison.Ordinal))
        {
            return "作業日と開始時刻を入力してください。";
        }
        if (message.Contains("A work event timestamp is required.", StringComparison.Ordinal))
        {
            return "記録日と記録時刻を入力してください。";
        }
        if (message.Contains("A work event timestamp cannot be in the future.", StringComparison.Ordinal))
        {
            return "未来の日時は記録できません。";
        }
        if (message.Contains("requires a completion event", StringComparison.Ordinal))
        {
            return "完了する前に完了記録を追加してください。";
        }
        if (message.Contains("not accept the requested event", StringComparison.Ordinal) ||
            message.Contains("read-only", StringComparison.OrdinalIgnoreCase))
        {
            return "この作業予定には指定した記録を追加できません。";
        }
        if (message.Contains("transition", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not allowed", StringComparison.OrdinalIgnoreCase))
        {
            return "この状態変更は実行できません。";
        }
        if (message.Contains("planned work order can be scheduled", StringComparison.Ordinal))
        {
            return "この作業予定は既に日程が決まっているため、もう一度日程を保存できません。";
        }

        return "処理を完了できませんでした。入力内容を確認してください。";
    }
}
