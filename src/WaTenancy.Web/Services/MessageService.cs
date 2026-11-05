using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WaTenancy.Web.Data;
using WaTenancy.Web.Infrastructure;
using WaTenancy.Web.Security;

namespace WaTenancy.Web.Services;

public sealed record SendRequest(Guid AccountId, string Recipient, string Text, Guid? FileId = null);
public sealed record JobView(Guid Id, Guid AccountId, string Recipient, string Text, Guid? FileId, JobState State, string? WhatsAppMessageId, string? Error, DateTime CreatedAt, DateTime? CompletedAt, int Attempts, DateTime NextAttemptAt);
public sealed class MessageService(IDbContextFactory<AppDbContext> factory, TenantAccess access, AppPaths paths, IOptions<AppOptions> options, IncomingMedia media)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> UploadLocks = new();
    public static string Recipient(string? value)
    {
        value = value?.Trim() ?? "";
        if (!Regex.IsMatch(value, "^\\+?[1-9][0-9]{6,14}$|^[0-9]{7,20}@(s\\.whatsapp\\.net|g\\.us|lid)$")) throw new AppException(400, "Use an international phone number or a WhatsApp chat JID.");
        return value;
    }
    public static JobView View(MessageJob x) => new(x.Id, x.AccountId, x.Recipient, x.Text, x.FileId, x.State, x.WhatsAppMessageId, x.Error, x.CreatedAt, x.CompletedAt, x.Attempts, x.NextAttemptAt);
    public async Task<JobView> Enqueue(ClaimsPrincipal p, Guid tenantId, SendRequest request, string key)
    {
        await access.Require(p, tenantId, TenantRole.Agent, "send");
        if (key.Length is < 8 or > 100 || key.Any(char.IsControl)) throw new AppException(400, "Provide an Idempotency-Key between 8 and 100 characters.");
        var recipient = Recipient(request.Recipient);
        var text = request.Text?.Trim() ?? "";
        if (text.Length > 4096 || text.Length == 0 && request.FileId is null) throw new AppException(400, "Provide a message up to 4096 characters, or an uploaded file.");
        var hash = SecretTokens.Hash(JsonSerializer.Serialize(new { request.AccountId, recipient, text, request.FileId }));
        await using var db = await factory.CreateDbContextAsync();
        var previous = await db.Jobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == key);
        if (previous is not null) return SameRequest(previous, hash);
        var account = await db.Accounts.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == request.AccountId) ?? throw new AppException(404, "Account not found.");
        if (request.FileId is not null && !await db.Files.AnyAsync(x => x.TenantId == tenantId && x.Id == request.FileId)) throw new AppException(404, "Uploaded file not found.");
        if (await db.Jobs.CountAsync(x => x.TenantId == tenantId && x.State == JobState.Queued) >= 1000) throw new AppException(429, "The company's outbound queue is full.");
        var job = new MessageJob { TenantId = tenantId, AccountId = request.AccountId, Recipient = recipient, Text = text, FileId = request.FileId, IdempotencyKey = key, RequestHash = hash };
        if(!account.WatchEnabled || account.State!=AccountState.Connected) job.Error="Queued, waiting for this number to connect and start watching.";
        db.Jobs.Add(job); TenantService.Audit(db, p, tenantId, "message.queued", job.Id.ToString());
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            await using var check = await factory.CreateDbContextAsync();
            previous = await check.Jobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == key);
            if (previous is not null) return SameRequest(previous, hash);
            throw;
        }
        return View(job);
    }
    private static JobView SameRequest(MessageJob previous, string hash)
    {
        if (previous.RequestHash != hash) throw new AppException(409, "This Idempotency-Key was already used for a different message.");
        return View(previous);
    }
    public async Task<List<JobView>> Jobs(ClaimsPrincipal p, Guid id, int page = 1)
    {
        await access.Require(p, id);
        await using var db = await factory.CreateDbContextAsync();
        return (await db.Jobs.Where(x => x.TenantId == id).OrderByDescending(x => x.CreatedAt).Skip((Math.Clamp(page, 1, 10000) - 1) * 50).Take(50).ToListAsync()).Select(View).ToList();
    }
    public async Task<JobView> Job(ClaimsPrincipal p, Guid id, Guid jobId)
    {
        await access.Require(p, id);
        await using var db = await factory.CreateDbContextAsync();
        return View(await db.Jobs.SingleOrDefaultAsync(x => x.TenantId == id && x.Id == jobId) ?? throw new AppException(404, "Message not found."));
    }
    public async Task Cancel(ClaimsPrincipal p, Guid id, Guid jobId)
    {
        await access.Require(p, id, TenantRole.Agent, "send");
        await using var db = await factory.CreateDbContextAsync();
        if (await db.Jobs.Where(x => x.TenantId == id && x.Id == jobId && x.State == JobState.Queued).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, JobState.Cancelled).SetProperty(x => x.CompletedAt, DateTime.UtcNow)) == 0) throw new AppException(409, "Only queued messages can be cancelled.");
        TenantService.Audit(db, p, id, "message.cancelled", jobId.ToString()); await db.SaveChangesAsync();
    }
    public async Task<JobView> Retry(ClaimsPrincipal p,Guid id,Guid jobId)
    {
        await access.Require(p,id,TenantRole.Agent,"send");
        await using var db=await factory.CreateDbContextAsync();
        var job=await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x=>x.TenantId==id&&x.Id==jobId)??throw new AppException(404,"Message not found.");
        if(job.State!=JobState.Failed || job.WhatsAppMessageId is not null) throw new AppException(409,"Only confirmed failed sends can be retried. Verify uncertain deliveries before creating a new message.");
        var updated=await db.Jobs.Where(x=>x.TenantId==id&&x.Id==jobId&&x.State==JobState.Failed&&x.WhatsAppMessageId==null)
            .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.State,JobState.Queued).SetProperty(x=>x.Attempts,0).SetProperty(x=>x.NextAttemptAt,DateTime.UtcNow).SetProperty(x=>x.Error,(string?)null).SetProperty(x=>x.CompletedAt,(DateTime?)null));
        if(updated==0) throw new AppException(409,"This message was already requeued.");
        TenantService.Audit(db,p,id,"message.retried",jobId.ToString());await db.SaveChangesAsync();
        return View((await db.Jobs.AsNoTracking().SingleAsync(x=>x.Id==jobId)));
    }
    public async Task<List<InboxMessage>> Inbox(ClaimsPrincipal p, Guid id, Guid? account = null, string? search = null, int page = 1)
    {
        await access.Require(p, id);
        await using var db = await factory.CreateDbContextAsync();
        var query = db.Inbox.Where(x => x.TenantId == id);
        if (account.HasValue) query = query.Where(x => x.AccountId == account);
        if (!string.IsNullOrWhiteSpace(search)) { search = search.Trim()[..Math.Min(search.Trim().Length, 100)]; query = query.Where(x => x.Text.Contains(search) || x.ChatName.Contains(search) || x.ChatJid.Contains(search)); }
        var messages = await query.OrderByDescending(x => x.Timestamp).Skip((Math.Clamp(page, 1, 10000) - 1) * 50).Take(50).ToListAsync();
        var accountIds = messages.Select(x => x.AccountId).Distinct().ToArray();
        var messageIds = messages.Select(x => x.WhatsAppMessageId).Distinct().ToArray();
        var attachments = await db.Attachments.AsNoTracking().Where(x => x.TenantId == id && x.State != "Purged" && accountIds.Contains(x.AccountId) && messageIds.Contains(x.MessageId)).ToListAsync();
        foreach(var message in messages)
            if(attachments.FirstOrDefault(x => x.AccountId == message.AccountId && x.ChatJid == message.ChatJid && x.MessageId == message.WhatsAppMessageId) is {} attachment) message.Attachment=media.View(attachment);
        return messages;
    }
    public async Task<StoredFile> Upload(ClaimsPrincipal p, Guid id, string name, string mime, Stream stream, long length, CancellationToken ct = default)
    {
        await access.Require(p, id, TenantRole.Agent, "send");
        if (length <= 0 || length > options.Value.MaxUploadBytes) throw new AppException(400, $"File limit is {options.Value.MaxUploadBytes / 1024 / 1024} MB.");
        name = Path.GetFileName(name.Replace('\\', '/'));
        if (name.Length is < 1 or > 200) throw new AppException(400, "Invalid filename.");
        var gate = UploadLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1)); await gate.WaitAsync(ct);
        var file = new StoredFile { TenantId = id, Name = name, ContentType = SafeMime(mime), Length = length };
        var path = paths.FilePath(id, file.Id);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if ((await db.Files.Where(x => x.TenantId == id).SumAsync(x => (long?)x.Length, ct) ?? 0) + length > options.Value.MaxTenantUploadBytes) throw new AppException(409, "This company's upload storage limit has been reached.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long total = 0; int count;
                while ((count = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    total += count;
                    if (total > length || total > options.Value.MaxUploadBytes) throw new AppException(400, "Upload exceeded its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                if (total != length) throw new AppException(400, "Upload was incomplete.");
            }
            db.Files.Add(file); TenantService.Audit(db, p, id, "file.uploaded", file.Id.ToString()); await db.SaveChangesAsync(ct); return file;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
        finally { gate.Release(); }
    }
    public async Task<StoredFile> FileInfo(ClaimsPrincipal p, Guid id, Guid fileId)
    {
        await access.Require(p, id);
        await using var db = await factory.CreateDbContextAsync();
        return await db.Files.SingleOrDefaultAsync(x => x.TenantId == id && x.Id == fileId) ?? throw new AppException(404, "File not found.");
    }
    private static string SafeMime(string mime) => mime is "image/jpeg" or "image/png" or "image/webp" or "video/mp4" or "audio/mpeg" or "audio/ogg" or "application/pdf" or "text/plain" ? mime : "application/octet-stream";
}
