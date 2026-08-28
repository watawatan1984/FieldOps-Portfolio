using System.Text.Encodings.Web;

namespace FieldOps.Web.Services;

public static class SafeHtmlErrorResponse
{
    public static async Task WriteAsync(HttpContext context, int statusCode, string correlationId)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
        string safeId = HtmlEncoder.Default.Encode(correlationId);
        await context.Response.WriteAsync($$"""
            <!doctype html><html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>エラー - FieldOps 業務ポータル</title><link rel="stylesheet" href="/lib/bootstrap/dist/css/bootstrap.min.css"><link rel="stylesheet" href="/css/site.css"></head><body><main class="container py-5"><h1>処理を完了できませんでした</h1><p>時間をおいて、もう一度お試しください。</p><p>お問い合わせ番号: <code>{{safeId}}</code></p><a class="btn btn-primary" href="/">ホームへ戻る</a></main></body></html>
            """);
    }
}
