using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Antiforgery;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:5190");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 512 * 1024);
var secret = builder.Configuration["Receiver:SigningSecret"];
if (string.IsNullOrEmpty(secret)) throw new InvalidOperationException("Set Receiver__SigningSecret to your company's webhook signing secret.");
if (secret.StartsWith("wat_", StringComparison.Ordinal)) throw new InvalidOperationException("Receiver:SigningSecret requires the signing secret from the company's Webhooks page, not a wat_ API key.");
var root = Path.GetFullPath(builder.Configuration["Receiver:DataRoot"] ?? "receiver-data", builder.Environment.ContentRootPath);
Directory.CreateDirectory(root);
var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "events.db") }.ToString();
builder.Services.AddAntiforgery(o => { o.HeaderName="X-CSRF-TOKEN"; o.Cookie.SameSite=SameSiteMode.Strict; });
var inbox=new InboxStore(connectionString);
builder.Services.AddSingleton(inbox); builder.Services.AddSingleton<ReceiverGateway>();
await using (var db = new SqliteConnection(connectionString))
{
    await db.OpenAsync(); await using var command = db.CreateCommand();
    command.CommandText = "CREATE TABLE IF NOT EXISTS events (event_id TEXT PRIMARY KEY, event_type TEXT NOT NULL, tenant_id TEXT NOT NULL, body TEXT NOT NULL, received_at TEXT NOT NULL, processed_at TEXT NULL); PRAGMA journal_mode=WAL;";
    await command.ExecuteNonQueryAsync();
}
await inbox.Initialize();
var app = builder.Build();
app.Use(async (context,next) =>
{
    if (context.Request.Path=="/" || context.Request.Path.StartsWithSegments("/inbox"))
    {
        if (!app.Environment.IsDevelopment() || context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)
            || context.Request.Headers.ContainsKey("Forwarded") || context.Request.Headers.ContainsKey("X-Forwarded-For")) { context.Response.StatusCode=404; return; }
        context.Response.Headers["Cache-Control"]="no-store";
        context.Response.Headers["X-Frame-Options"]="DENY";
        context.Response.Headers["X-Content-Type-Options"]="nosniff";
    }
    await next();
});
app.UseStaticFiles();
app.MapGet("/", () => Results.File(Path.Combine(app.Environment.ContentRootPath,"wwwroot","inbox","index.html"),"text/html"));
app.MapGet("/inbox/config", (HttpContext ctx,IAntiforgery antiforgery,ReceiverGateway gateway) => Results.Ok(new { canSend=gateway.CanSend,csrfToken=antiforgery.GetAndStoreTokens(ctx).RequestToken }));
app.MapGet("/inbox/stats", () => inbox.Info());
app.MapGet("/inbox/threads", async (string? search,int? page) => { var rows=await inbox.Threads(search is { Length:>200 } ? search[..200] : search,page??0); return Results.Ok(new { items=rows.Take(50),hasMore=rows.Count>50 }); });
app.MapGet("/inbox/messages", async (Guid tenantId,Guid accountId,string chatJid,int? take,ReceiverGateway gateway,HttpContext ctx) => { await gateway.Refresh(inbox,ctx.RequestAborted); return Results.Ok(await inbox.Messages(tenantId,accountId,chatJid,take??50)); });
app.MapGet("/inbox/events", () => inbox.Events());
app.MapGet("/inbox/media/{id:guid}", async (Guid id,ReceiverGateway gateway,HttpContext ctx) =>
{
    if(await inbox.AttachmentTenant(id) is not {} tenant){ctx.Response.StatusCode=404;return;}
    await gateway.Media(tenant,id,ctx);
});
app.MapGet("/inbox/events/{id:guid}", async (Guid id) => await inbox.Event(id) is { } body ? Results.Content(body,"application/json") : Results.NotFound());
app.MapPost("/inbox/reply", async (ReceiverReply reply,HttpContext ctx,IAntiforgery antiforgery,ReceiverGateway gateway) =>
{
    try { await antiforgery.ValidateRequestAsync(ctx); } catch(AntiforgeryValidationException) { return Results.BadRequest(new { error="Refresh the page before sending." }); }
    if (reply.Text is null || reply.Text.Trim().Length is <1 or >4096 || reply.ChatJid is null || reply.ChatJid.Length>200 || !Guid.TryParse(reply.RequestId,out _)) return Results.BadRequest(new { error="Provide a message of 1–4096 characters and a valid request ID." });
    if (!await inbox.HasThread(reply.TenantId,reply.AccountId,reply.ChatJid)) return Results.NotFound(new { error="Conversation not found." });
    var result=await gateway.Reply(reply.TenantId,reply.AccountId,reply.ChatJid,reply.Text.Trim(),reply.RequestId,ctx.RequestAborted);
    if(result.Status!=202) return Results.Json(new { error=result.Body },statusCode:result.Status);
    using var job=JsonDocument.Parse(result.Body); await inbox.SaveReply(reply.TenantId,reply.AccountId,reply.ChatJid,reply.Text.Trim(),job.RootElement);
    return Results.Content(result.Body,"application/json",statusCode:202);
});
app.MapPost("/inbox/retry",async(ReceiverRetry retry,HttpContext ctx,IAntiforgery antiforgery,ReceiverGateway gateway)=>
{
    try{await antiforgery.ValidateRequestAsync(ctx);}catch(AntiforgeryValidationException){return Results.BadRequest(new{error="Refresh the page before retrying."});}
    if(!await inbox.HasReply(retry.TenantId,retry.AccountId,retry.JobId))return Results.NotFound(new{error="Reply not found."});
    var result=await gateway.Retry(retry.TenantId,retry.JobId,ctx.RequestAborted);
    if(result.Status!=202)return Results.Json(new{error=result.Body},statusCode:result.Status);
    using var job=JsonDocument.Parse(result.Body);await inbox.UpdateReplies(JsonSerializer.SerializeToElement(new[]{job.RootElement}));
    return Results.Content(result.Body,"application/json",statusCode:202);
});
app.MapPost("/whatsapp/events", async (HttpContext context) =>
{
    var timestamp = context.Request.Headers["X-WaTenancy-Timestamp"].ToString();
    if (!long.TryParse(timestamp, out var seconds) || Math.Abs((decimal)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds) > 300) return Results.Unauthorized();
    var signature = context.Request.Headers["X-WaTenancy-Signature"].ToString();
    if (signature.Length != 71 || !signature.StartsWith("sha256=", StringComparison.Ordinal)) return Results.Unauthorized();
    using var buffer = new MemoryStream(); var chunk = new byte[8192]; int read;
    while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
    {
        if (buffer.Length + read > 512 * 1024) return Results.StatusCode(413);
        await buffer.WriteAsync(chunk.AsMemory(0, read), context.RequestAborted);
    }
    var raw = buffer.ToArray();
    var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
    var signed = new byte[prefix.Length + raw.Length]; prefix.CopyTo(signed, 0); raw.CopyTo(signed, prefix.Length);
    var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
    try { if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signature[7..]))) return Results.Unauthorized(); }
    catch (FormatException) { return Results.Unauthorized(); }
    JsonDocument json;
    try { json = JsonDocument.Parse(raw); } catch (JsonException) { return Results.BadRequest(); }
    using (json)
    {
        var value = json.RootElement;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("eventId", out var idValue) || idValue.ValueKind != JsonValueKind.String || !idValue.TryGetGuid(out var id) || context.Request.Headers["X-WaTenancy-Event-Id"].ToString() != id.ToString()) return Results.BadRequest();
        if (!value.TryGetProperty("tenantId", out var tenantValue) || tenantValue.ValueKind != JsonValueKind.String || !tenantValue.TryGetGuid(out var tenant) || !value.TryGetProperty("eventType", out var typeValue) || typeValue.ValueKind != JsonValueKind.String) return Results.BadRequest();
        var type = typeValue.GetString()!;
        await using var db = new SqliteConnection(connectionString); await db.OpenAsync(context.RequestAborted);
        await using var transaction=(SqliteTransaction)await db.BeginTransactionAsync(context.RequestAborted);
        await using var command = db.CreateCommand();
        command.Transaction=transaction;
        command.CommandText = "INSERT OR IGNORE INTO events(event_id,event_type,tenant_id,body,received_at) VALUES($id,$type,$tenant,$body,$at)";
        command.Parameters.AddWithValue("$id", id.ToString()); command.Parameters.AddWithValue("$type", type); command.Parameters.AddWithValue("$tenant", tenant.ToString());
        command.Parameters.AddWithValue("$body", Encoding.UTF8.GetString(raw)); command.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        var inserted = await command.ExecuteNonQueryAsync(context.RequestAborted);
        if (inserted==1) await InboxStore.Project(db,transaction,value);
        await transaction.CommitAsync(context.RequestAborted);
        if (inserted == 1) app.Logger.LogInformation("Persisted event {EventId}, type {EventType}; ready for background processing.", id, type);
        // Add a background worker that processes rows where processed_at IS NULL.
        // Persist business effects idempotently, then set processed_at. Never block the HTTP acknowledgment on slow work.
        return Results.Ok(new { acknowledged = true, duplicate = inserted == 0 });
    }
});
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.Run();
public sealed record ReceiverReply(Guid TenantId,Guid AccountId,string ChatJid,string Text,string RequestId);
public sealed record ReceiverRetry(Guid TenantId,Guid AccountId,Guid JobId);
