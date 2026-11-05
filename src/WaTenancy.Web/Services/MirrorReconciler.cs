using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WaTenancy.Web.Data;
using WaTenancy.Web.Infrastructure;

namespace WaTenancy.Web.Services;

public sealed class MirrorReconciler(IDbContextFactory<AppDbContext> factory, AppPaths paths, IncomingEvents incoming, WebhookService webhooks, IOptions<AppOptions> options, ILogger<MirrorReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.EnableWorkers) return;
        while (!ct.IsCancellationRequested)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var accounts = await db.Accounts.Where(x => x.Tenant.IsEnabled && x.WatchEnabled).ToListAsync(ct);
            foreach (var account in accounts)
            {
                try { await Reconcile(account, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Read-only mirror reconciliation failed for {AccountId} ({ExceptionType}).", account.Id, ex.GetType().Name); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { break; }
        }
    }
    public async Task Reconcile(WhatsAppAccount account, CancellationToken ct)
    {
        var path = Path.Combine(paths.Store(account.TenantId, account.Id), "wacli.db");
        if (!File.Exists(path)) return;
        await using var mirror = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await mirror.OpenAsync(ct);
        var columns = new HashSet<string>();
        await using (var schema = mirror.CreateCommand())
        {
            schema.CommandText = "PRAGMA table_info(messages)";
            await using var reader = await schema.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(1));
        }
        string Column(string name, string fallback) => columns.Contains(name) ? $"COALESCE({name},{fallback})" : fallback;
        await using (var command = mirror.CreateCommand())
        {
            command.CommandText = $"SELECT rowid,chat_jid,msg_id,COALESCE(chat_name,''),COALESCE(sender_jid,''),ts,COALESCE(display_text,text,''),from_me,deleted_at,payload_purged_at,{Column("media_type", "''")},{Column("mime_type", "''")},{Column("filename", "''")},{Column("file_length", "0")} FROM messages WHERE rowid > $cursor ORDER BY rowid LIMIT 1000";
            command.Parameters.AddWithValue("$cursor", account.MirrorCursor);
            await using var reader = await command.ExecuteReaderAsync(ct);
            long cursor = account.MirrorCursor;
            while (await reader.ReadAsync(ct))
            {
                cursor = reader.GetInt64(0);
                if (!reader.IsDBNull(8) || !reader.IsDBNull(9)) continue;
                var json = JsonSerializer.SerializeToElement(new { Chat = reader.GetString(1), ID = reader.GetString(2), ChatName = reader.GetString(3), SenderJID = reader.GetString(4), Timestamp = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)).UtcDateTime.ToString("O"), Text = reader.GetString(6), FromMe = reader.GetInt64(7) != 0, Media = new { Type = reader.GetString(10), MimeType = reader.GetString(11), Filename = reader.GetString(12), FileLength = reader.GetInt64(13) } });
                await incoming.Ingest(account.Id, json);
            }
            if (cursor > account.MirrorCursor)
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                await db.Accounts.Where(x => x.Id == account.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.MirrorCursor, cursor), ct);
            }
        }
        // Reconcile tombstones and the persistent purge ledger without modifying wacli's databases.
        await using var tombstones = mirror.CreateCommand();
        tombstones.CommandText = "SELECT chat_jid,msg_id FROM messages WHERE deleted_at IS NOT NULL OR payload_purged_at IS NOT NULL UNION SELECT chat_jid,msg_id FROM message_payload_purges";
        await using var deleted = await tombstones.ExecuteReaderAsync(ct);
        while (await deleted.ReadAsync(ct))
        {
            var chat = deleted.GetString(0); var message = deleted.GetString(1);
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = await db.Inbox.Where(x => x.AccountId == account.Id && x.ChatJid == chat && x.WhatsAppMessageId == message).ExecuteDeleteAsync(ct);
            if (rows == 0) continue;
            var prefix = $"message:{account.Id}:{chat}:{message}:";
            var attachments = await db.Attachments.Where(x => x.AccountId == account.Id && x.ChatJid == chat && x.MessageId == message).ToListAsync(ct);
            foreach (var attachment in attachments)
            {
                attachment.State = "Purged";
                var attachmentPath = paths.AttachmentPath(attachment.TenantId, attachment.Id);
                if (File.Exists(attachmentPath)) File.Delete(attachmentPath);
                await db.WebhookDeliveries.Where(x => x.TenantId == account.TenantId && (x.DeduplicationKey == "media-ready:" + attachment.Id || x.DeduplicationKey == "media-failed:" + attachment.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, "{}").SetProperty(x => x.State, "Purged"), ct);
            }
            await db.WebhookDeliveries.Where(x => x.TenantId == account.TenantId && x.DeduplicationKey.StartsWith(prefix)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, "{}").SetProperty(x => x.State, "Purged"), ct);
            await webhooks.Queue(db, account.TenantId, account.Id, "message.deleted", new { messageId = message, chatJid = chat }, "deleted:" + prefix);
            await db.SaveChangesAsync(ct);
        }
    }
}
