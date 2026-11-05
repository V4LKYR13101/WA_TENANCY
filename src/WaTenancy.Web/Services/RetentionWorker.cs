using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WaTenancy.Web.Data;
using WaTenancy.Web.Infrastructure;

namespace WaTenancy.Web.Services;

public sealed class RetentionWorker(IDbContextFactory<AppDbContext> factory, AppPaths paths, IOptions<AppOptions> options, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                var cutoff = DateTime.UtcNow.AddDays(-options.Value.RetentionDays);
                await db.Inbox.Where(x => x.Timestamp < cutoff).ExecuteDeleteAsync(ct);
                var media = await db.Attachments.Where(x => x.CreatedAt < cutoff || !db.Inbox.Any(m => m.AccountId == x.AccountId && m.ChatJid == x.ChatJid && m.WhatsAppMessageId == x.MessageId)).ToListAsync(ct);
                foreach (var attachment in media)
                {
                    var mediaPath = paths.AttachmentPath(attachment.TenantId, attachment.Id);
                    if (File.Exists(mediaPath)) File.Delete(mediaPath);
                    db.Attachments.Remove(attachment);
                }
                await db.Audit.Where(x => x.Timestamp < cutoff).ExecuteDeleteAsync(ct);
                await db.Receipts.Where(x => x.Timestamp < cutoff).ExecuteDeleteAsync(ct);
                await db.WebhookDeliveries.Where(x => x.CreatedAt < cutoff && x.State != "Sending").ExecuteDeleteAsync(ct);
                await db.Jobs.Where(x => x.CreatedAt < cutoff && x.State != JobState.Dispatching && x.State != JobState.Queued).ExecuteDeleteAsync(ct);
                var files = await db.Files.Where(x => x.CreatedAt < cutoff && !db.Jobs.Any(j => j.FileId == x.Id)).ToListAsync(ct);
                foreach (var file in files) { var path = paths.FilePath(file.TenantId, file.Id); if (File.Exists(path)) File.Delete(path); db.Files.Remove(file); }
                await db.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError("Retention cleanup failed ({ExceptionType}).", ex.GetType().Name); }
            try { if (!await timer.WaitForNextTickAsync(ct)) break; } catch (OperationCanceledException) { break; }
        }
    }
}
