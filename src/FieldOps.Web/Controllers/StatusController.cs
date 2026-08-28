using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FieldOps.Web.Controllers;

[AllowAnonymous]
[Route("status")]
public sealed class StatusController : Controller
{
    [AcceptVerbs("GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", Route = "{code:int}")]
    public IActionResult Index(int code)
    {
        StatusPageViewModel? model = code switch
        {
            StatusCodes.Status403Forbidden => new StatusPageViewModel(
                code,
                "この操作を行う権限がありません",
                "ログイン中の役割では、この情報を表示または変更できません。",
                "ホームへ戻る"),
            StatusCodes.Status404NotFound => new StatusPageViewModel(
                code,
                "指定された情報が見つかりません",
                "情報が削除されたか、表示できる範囲の外にあります。",
                "一覧へ戻る"),
            _ => null
        };

        if (model is null)
        {
            return StatusCode(code);
        }

        Response.StatusCode = model.StatusCode;
        return View(model);
    }
}

public sealed record StatusPageViewModel(
    int StatusCode,
    string Title,
    string Description,
    string PrimaryActionLabel);
