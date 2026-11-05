using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WaTenancy.Web.Data;
using WaTenancy.Web.Infrastructure;
using WaTenancy.Web.Security;

namespace WaTenancy.Web.Services;

public sealed class IncomingMedia(IDbContextFactory<AppDbContext> factory, TenantAccess access, AppPaths paths, IOptions<AppOptions> options)
{
    public bool WithinRetention(DateTime timestamp) => timestamp >= DateTime.UtcNow.AddDays(-options.Value.RetentionDays);
    public object View(IncomingAttachment file) => new
    {
        id = file.Id, file.AccountId, file.MessageId, file.ChatJid, file.MediaType,
        file.ContentType, file.Name, file.Length, file.State, file.Error,
        downloadUrl = Url(file.Id), metadataUrl = Url(file.Id) + "/metadata"
    };
    private string Url(Guid id) => (options.Value.PublicBaseUrl?.TrimEnd('/') ?? "") + $"/api/v1/media/{id}";
    public async Task<IncomingAttachment> Find(ClaimsPrincipal principal, Guid tenant, Guid id)
    {
        await access.Require(principal, tenant);
        await using var db = await factory.CreateDbContextAsync();
        return await db.Attachments.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant && x.Id == id && x.State != "Purged" && db.Inbox.Any(m => m.AccountId == x.AccountId && m.ChatJid == x.ChatJid && m.WhatsAppMessageId == x.MessageId))
            ?? throw new AppException(404, "Attachment not found.");
    }
    public async Task Retry(ClaimsPrincipal principal, Guid tenant, Guid id)
    {
        await access.Require(principal, tenant, TenantRole.Agent, "send");
        var file = await Find(principal, tenant, id);
        if(file.State!="Failed") throw new AppException(409,"Only failed media downloads can be retried.");
        if(file.Length>options.Value.MaxIncomingMediaBytes) throw new AppException(409,"Attachment exceeds the media size limit.");
        await using var db=await factory.CreateDbContextAsync();
        if(await db.Attachments.Where(x=>x.TenantId==tenant && x.Id==id && x.State=="Failed").ExecuteUpdateAsync(s=>s.SetProperty(x=>x.State,"Pending").SetProperty(x=>x.Attempts,0).SetProperty(x=>x.Error,(string?)null).SetProperty(x=>x.NextAttemptAt,DateTime.UtcNow))==0)
            throw new AppException(409,"Attachment was already requeued.");
        TenantService.Audit(db,principal,tenant,"media.retried",id.ToString());await db.SaveChangesAsync();
    }
    public async Task<IncomingAttachment?> Queue(AppDbContext db, WhatsAppAccount account, string chat, string message, JsonElement media)
    {
        var type = CliJson.String(media, "Type", "type").ToLowerInvariant();
        if (type is not ("audio" or "image" or "video" or "document" or "sticker")) return null;
        var previous = await db.Attachments.SingleOrDefaultAsync(x => x.AccountId == account.Id && x.ChatJid == chat && x.MessageId == message);
        if (previous is not null) return previous.State == "Purged" ? null : previous;
        long length = 0;
        if (media.TryGetProperty("FileLength", out var size)) size.TryGetInt64(out length);
        var mime = SafeMime(CliJson.String(media, "MimeType", "mimeType"));
        var name = Path.GetFileName(CliJson.String(media, "Filename", "filename").Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name)) name = type == "audio" ? "voice-note.ogg" : "attachment";
        name = new string(name.Where(c => !char.IsControl(c)).Take(200).ToArray());
        var file = new IncomingAttachment { TenantId = account.TenantId, AccountId = account.Id, ChatJid = chat, MessageId = message, MediaType = type, ContentType = mime, Name = name, Length = Math.Max(0, length) };
        if (length > options.Value.MaxIncomingMediaBytes) { file.State = "Failed"; file.Error = "Attachment exceeds the incoming media size limit."; }
        db.Attachments.Add(file); return file;
    }
    public static string SafeMime(string value)
    {
        var mime = value.Split(';')[0].Trim().ToLowerInvariant();
        return mime is "audio/ogg" or "audio/mpeg" or "audio/mp4" or "audio/aac" or "audio/wav" or "audio/webm" or "image/jpeg" or "image/png" or "image/webp" or "video/mp4" or "application/pdf" ? mime : "application/octet-stream";
    }
    public string PathFor(IncomingAttachment file) => paths.AttachmentPath(file.TenantId, file.Id);
}

public sealed class IncomingMediaWorker(IDbContextFactory<AppDbContext> factory, IWacliClient cli, AppPaths paths, IncomingMedia media, WebhookService hooks, IOptions<AppOptions> options, ILogger<IncomingMediaWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.EnableWorkers) return;
        var active = new Dictionary<Guid, Task>();
        await using (var db = await factory.CreateDbContextAsync(ct))
            await db.Attachments.Where(x => x.State == "Downloading").ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Pending"), ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    foreach (var tenant in active.Where(x => x.Value.IsCompleted).Select(x => x.Key).ToArray())
                    { var task=active[tenant]; active.Remove(tenant); await task; }
                    await using var db = await factory.CreateDbContextAsync(ct);
                    var now = DateTime.UtcNow;
                    var busy = active.Keys.ToArray();
                    var tenants = await db.Attachments.Where(x => x.State == "Pending" && x.NextAttemptAt <= now && x.Account.Tenant.IsEnabled && !busy.Contains(x.TenantId))
                        .Select(x => x.TenantId).Distinct().Take(Math.Clamp(options.Value.MaxConcurrentMediaDownloads, 1, 16) - active.Count).ToListAsync(ct);
                    foreach (var tenant in tenants)
                    {
                        var file = await db.Attachments.AsNoTracking().Where(x => x.TenantId == tenant && x.State == "Pending" && x.NextAttemptAt <= now).OrderBy(x => x.NextAttemptAt).FirstAsync(ct);
                        active[tenant] = Download(file.Id, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogWarning("Incoming media scheduling failed ({ExceptionType}).", ex.GetType().Name); }
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            try { await Task.WhenAll(active.Values); }
            catch(Exception ex) { logger.LogWarning("Media shutdown completed with an interrupted download ({ExceptionType}).",ex.GetType().Name); }
        }
    }
    public async Task Download(Guid id, CancellationToken ct)
    {
        string? temp = null;
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (await db.Attachments.Where(x => x.Id == id && x.State == "Pending").ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Downloading").SetProperty(x => x.Attempts, x => x.Attempts + 1), ct) == 0) return;
            var file = await db.Attachments.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            var path = paths.AttachmentPath(file.TenantId, file.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temp = path + ".part";
            if (File.Exists(temp)) File.Delete(temp);
            string? error = null;
            var terminal = false;
            var stored = await db.Attachments.Where(x => x.TenantId == file.TenantId && x.State == "Ready").SumAsync(x => (long?)x.Length, ct) ?? 0;
            if (stored + file.Length > options.Value.MaxTenantIncomingMediaBytes) { error = "Company incoming media storage limit reached."; terminal = true; }
            else
            {
                var result = await cli.Run(paths.Store(file.TenantId, file.AccountId), ["--read-only", "--json", "media", "download", "--chat", file.ChatJid, "--id", file.MessageId, "--output", temp], ct);
                if (result.ExitCode != 0 || result.TimedOut || !File.Exists(temp)) error = "Audio or attachment download is unavailable. Retrying automatically.";
                else
                {
                    var output = CliJson.Parse(result.Output);
                    if (!CliJson.Bool(output, "downloaded")) error = "Media download was not confirmed.";
                    else
                    {
                        file.Length = new FileInfo(temp).Length;
                        var mime = CliJson.String(output, "mime_type");
                        if (!string.IsNullOrEmpty(mime)) file.ContentType = IncomingMedia.SafeMime(mime);
                        if (file.Length == 0 || file.Length > options.Value.MaxIncomingMediaBytes || stored + file.Length > options.Value.MaxTenantIncomingMediaBytes)
                        { error = "Attachment is empty or exceeds the incoming media storage limit."; terminal = true; }
                    }
                }
            }
            // Re-read after network work: deletion/retention may revoke the attachment in the meantime.
            await using var finish = await factory.CreateDbContextAsync(CancellationToken.None);
            await using var transaction = await finish.Database.BeginTransactionAsync(CancellationToken.None);
            var target = await finish.Attachments.SingleOrDefaultAsync(x => x.Id == id && x.State == "Downloading");
            if (target is null) return;
            if (!await finish.Inbox.AnyAsync(x => x.AccountId == file.AccountId && x.ChatJid == file.ChatJid && x.WhatsAppMessageId == file.MessageId))
            { target.State = "Purged"; await finish.SaveChangesAsync(); await transaction.CommitAsync(); return; }
            if (error is null)
            {
                File.Move(temp, path, true);
                target.State = "Ready"; target.Error = null; target.Length = file.Length; target.ContentType = file.ContentType;
                await hooks.Queue(finish, file.TenantId, file.AccountId, "message.media.ready", new { messageId = file.MessageId, chatJid = file.ChatJid, attachment = media.View(target) }, "media-ready:" + id);
            }
            else
            {
                target.State = terminal || target.Attempts >= Math.Clamp(options.Value.MaxMediaAttempts, 1, 20) ? "Failed" : "Pending";
                target.Error = error; target.NextAttemptAt = DateTime.UtcNow.AddSeconds(Math.Min(300, 5 * Math.Pow(2, target.Attempts - 1)));
                if (target.State == "Failed") await hooks.Queue(finish, file.TenantId, file.AccountId, "message.media.failed", new { messageId = file.MessageId, chatJid = file.ChatJid, attachment = media.View(target) }, "media-failed:" + id);
            }
            await finish.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Incoming media attempt failed for {AttachmentId} ({ExceptionType}).", id, ex.GetType().Name);
            try
            {
                await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
                var failed = await db.Attachments.SingleOrDefaultAsync(x => x.Id == id && x.State == "Downloading");
                if (failed is not null)
                {
                    failed.State = failed.Attempts >= Math.Clamp(options.Value.MaxMediaAttempts, 1, 20) ? "Failed" : "Pending";
                    failed.Error = "Download interrupted or unavailable."; failed.NextAttemptAt = DateTime.UtcNow.AddSeconds(30);
                    if (failed.State == "Failed") await hooks.Queue(db, failed.TenantId, failed.AccountId, "message.media.failed", new { messageId = failed.MessageId, chatJid = failed.ChatJid, attachment = media.View(failed) }, "media-failed:" + id);
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception persist) { logger.LogWarning("Could not persist media retry ({ExceptionType}).", persist.GetType().Name); }
        }
        finally { if (temp is not null && File.Exists(temp)) try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning("Temporary media cleanup failed ({ExceptionType}).",ex.GetType().Name); } }
    }
}
