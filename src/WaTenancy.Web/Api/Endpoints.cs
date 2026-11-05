using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using WaTenancy.Web.Infrastructure;
using WaTenancy.Web.Security;
using WaTenancy.Web.Services;
namespace WaTenancy.Web.Api;
public sealed record CreateAccountRequest(string Name);
public sealed record PairRequest(string? Phone);
public sealed record WatchRequest(bool Enabled);
public static class Endpoints
{
    private static Guid Tenant(ClaimsPrincipal p) => Guid.Parse(p.FindFirst("tenant_id")!.Value);
    public static void MapTenantApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = ApiKeyAuthentication.SchemeName }).RequireRateLimiting("api");
        api.MapGet("/me", (ClaimsPrincipal p) => Results.Ok(new { tenantId = Tenant(p), apiKeyId = p.FindFirst("api_key_id")!.Value }));
        api.MapGet("/accounts", async (ClaimsPrincipal p, TenantService service) => Results.Ok(await service.Accounts(p, Tenant(p))));
        api.MapGet("/accounts/{id:guid}", async (Guid id, ClaimsPrincipal p, TenantService service) => (await service.Accounts(p, Tenant(p))).FirstOrDefault(x => x.Id == id) is { } account ? Results.Ok(account) : Results.NotFound());
        api.MapPost("/accounts", async (CreateAccountRequest req, ClaimsPrincipal p, TenantService service) => { var account = await service.CreateAccount(p, Tenant(p), req.Name); return Results.Created($"/api/v1/accounts/{account.Id}", new { account.Id, account.Name, account.State }); });
        api.MapPost("/accounts/{id:guid}/pair", async (Guid id, PairRequest req, ClaimsPrincipal p, AccountSupervisor supervisor) => { await supervisor.StartPairing(p, Tenant(p), id, req.Phone); return Results.Accepted($"/api/v1/accounts/{id}/pairing"); });
        api.MapGet("/accounts/{id:guid}/pairing", async (Guid id, ClaimsPrincipal p, AccountSupervisor supervisor) => Results.Ok(await supervisor.Pairing(p, Tenant(p), id)));
        api.MapPost("/accounts/{id:guid}/watch", async (Guid id, WatchRequest req, ClaimsPrincipal p, AccountSupervisor supervisor) => { await supervisor.SetWatch(p, Tenant(p), id, req.Enabled); return Results.NoContent(); });
        api.MapPost("/accounts/{id:guid}/logout", async (Guid id, ClaimsPrincipal p, AccountSupervisor supervisor) => { await supervisor.Logout(p, Tenant(p), id); return Results.NoContent(); });
        api.MapPost("/messages", async (SendRequest req, HttpContext ctx, MessageService service) => { var job = await service.Enqueue(ctx.User, Tenant(ctx.User), req, ctx.Request.Headers["Idempotency-Key"].ToString()); return Results.Accepted($"/api/v1/jobs/{job.Id}", job); });
        api.MapGet("/jobs", async (int? page, ClaimsPrincipal p, MessageService service) => Results.Ok(await service.Jobs(p, Tenant(p), page ?? 1)));
        api.MapGet("/jobs/{id:guid}", async (Guid id, ClaimsPrincipal p, MessageService service) => Results.Ok(await service.Job(p, Tenant(p), id)));
        api.MapPost("/jobs/{id:guid}/cancel", async (Guid id, ClaimsPrincipal p, MessageService service) => { await service.Cancel(p, Tenant(p), id); return Results.NoContent(); });
        api.MapPost("/jobs/{id:guid}/retry", async (Guid id, ClaimsPrincipal p, MessageService service) => Results.Accepted($"/api/v1/jobs/{id}",await service.Retry(p,Tenant(p),id)));
        api.MapGet("/inbox", async (Guid? accountId, string? search, int? page, ClaimsPrincipal p, MessageService service) => Results.Ok(await service.Inbox(p, Tenant(p), accountId, search, page ?? 1)));
        api.MapGet("/media/{id:guid}/metadata", async (Guid id, ClaimsPrincipal p, IncomingMedia media) => Results.Ok(media.View(await media.Find(p, Tenant(p), id))));
        api.MapPost("/media/{id:guid}/retry", async (Guid id,ClaimsPrincipal p,IncomingMedia media)=>{await media.Retry(p,Tenant(p),id);return Results.Accepted($"/api/v1/media/{id}/metadata");});
        api.MapGet("/media/{id:guid}", async (Guid id, HttpContext ctx, IncomingMedia media) =>
        {
            var file = await media.Find(ctx.User, Tenant(ctx.User), id);
            ctx.Response.Headers["Cache-Control"] = "private, no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (file.State != "Ready") return Results.Json(new { title = "Attachment is not ready.", attachment = media.View(file) }, statusCode:409);
            return File.Exists(media.PathFor(file)) ? Results.File(media.PathFor(file), file.ContentType, file.Name, enableRangeProcessing:true) : Results.NotFound();
        });
        api.MapPost("/files", async (HttpContext ctx, MessageService service) =>
        {
            var length = ctx.Request.ContentLength ?? throw new AppException(411, "Provide Content-Length.");
            var file = await service.Upload(ctx.User, Tenant(ctx.User), ctx.Request.Headers["X-Filename"].ToString(), ctx.Request.ContentType ?? "application/octet-stream", ctx.Request.Body, length, ctx.RequestAborted);
            return Results.Created($"/api/v1/files/{file.Id}", new { file.Id, file.Name, file.Length, file.ContentType });
        });
        api.MapGet("/files/{id:guid}", async (Guid id, ClaimsPrincipal p, MessageService service, AppPaths paths) => { var tenant = Tenant(p); var file = await service.FileInfo(p, tenant, id); return File.Exists(paths.FilePath(tenant, id)) ? Results.File(paths.FilePath(tenant, id), "application/octet-stream", file.Name) : Results.NotFound(); });
        app.MapPost("/internal/wacli/{id:guid}", async (Guid id, HttpContext ctx, IncomingEvents events) =>
        {
            if (ctx.Connection.RemoteIpAddress is { } ip && !IPAddress.IsLoopback(ip)) return Results.NotFound();
            if (ctx.Request.ContentLength > 512 * 1024) return Results.StatusCode(413);
            using var buffer = new MemoryStream(); var chunk = new byte[8192]; int read;
            while ((read = await ctx.Request.Body.ReadAsync(chunk, ctx.RequestAborted)) > 0) { if (buffer.Length + read > 512 * 1024) return Results.StatusCode(413); await buffer.WriteAsync(chunk.AsMemory(0, read), ctx.RequestAborted); }
            var raw = buffer.ToArray();
            if (!await events.Verify(id, raw, ctx.Request.Headers["X-Wacli-Signature"].ToString())) return Results.Unauthorized();
            using var json = JsonDocument.Parse(raw);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return Results.BadRequest(new { title = "Expected an event object." });
            await events.Ingest(id, json.RootElement); return Results.NoContent();
        }).AllowAnonymous().ExcludeFromDescription();
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
    }
}
