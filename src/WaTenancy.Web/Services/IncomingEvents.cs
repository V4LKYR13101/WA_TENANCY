using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WaTenancy.Web.Data;
using WaTenancy.Web.Infrastructure;
using WaTenancy.Web.Security;

namespace WaTenancy.Web.Services;

public sealed class IncomingEvents(IDbContextFactory<AppDbContext> factory, IDataProtectionProvider protection, WebhookService webhooks, IncomingMedia attachments)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    public async Task<bool> Verify(Guid accountId, byte[] raw, string signature)
    {
        await using var db = await factory.CreateDbContextAsync();
        var account = await db.Accounts.Include(x => x.Tenant).SingleOrDefaultAsync(x => x.Id == accountId);
        if (account is null || !account.Tenant.IsEnabled || signature.Length != 71 || !signature.StartsWith("sha256=", StringComparison.Ordinal)) return false;
        try
        {
            var secret = protection.CreateProtector("WaTenancy.Secrets.v1").Unprotect(account.ProtectedWebhookSecret);
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), raw);
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signature[7..]));
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }
    public async Task Ingest(Guid accountId, JsonElement value)
    {
        var gate = gates.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1)); await gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var account = await db.Accounts.Include(x => x.Tenant).SingleOrDefaultAsync(x => x.Id == accountId);
            if (account is null || !account.Tenant.IsEnabled) return;
            var eventType = CliJson.String(value, "EventType");
            if (eventType == "receipt")
            {
                var type = CliJson.String(value, "Type");
                var rank = type is "read" or "played" ? JobState.Read : JobState.Delivered;
                if (!value.TryGetProperty("MessageIDs", out var ids) || ids.ValueKind != JsonValueKind.Array) return;
                foreach (var id in ids.EnumerateArray().Take(1000))
                {
                    var messageId = id.GetString(); if (string.IsNullOrWhiteSpace(messageId) || messageId.Length > 200) continue;
                    var receipt = await db.Receipts.FindAsync(accountId, messageId);
                    if (receipt is null) db.Receipts.Add(new OutboundReceipt { AccountId = accountId, MessageId = messageId, State = rank });
                    else if (receipt.State < rank) receipt.State = rank;
                    await db.Jobs.Where(x => x.AccountId == accountId && x.WhatsAppMessageId == messageId && (x.State == JobState.Sent || x.State == JobState.Delivered)).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, rank));
                }
                await webhooks.Queue(db, account.TenantId, account.Id, "message.receipt", value, "receipt:" + accountId + ":" + SecretTokens.Hash(value.GetRawText()));
            }
            else if (eventType == "chat_presence")
                await webhooks.Queue(db, account.TenantId, account.Id, "chat.presence", value, "presence:" + Guid.NewGuid());
            else
            {
                var msgId = CliJson.String(value, "ID", "messageId"); var chat = CliJson.String(value, "Chat", "chatJid");
                if (msgId.Length is < 1 or > 200 || chat.Length is < 1 or > 200) return;
                var timestampString = CliJson.String(value, "Timestamp", "timestamp");
                var timestamp = DateTime.TryParse(timestampString, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTime.UtcNow;
                var text = CliJson.String(value, "Text", "text"); if (text.Length > 65536) text = text[..65536];
                var message = await db.Inbox.SingleOrDefaultAsync(x => x.AccountId == accountId && x.ChatJid == chat && x.WhatsAppMessageId == msgId);
                var updated = message is not null && message.Text != text;
                if (message is null)
                {
                    message = new InboxMessage { TenantId = account.TenantId, AccountId = accountId, WhatsAppMessageId = msgId, ChatJid = chat, Timestamp = timestamp, FromMe = CliJson.Bool(value, "FromMe", "fromMe") };
                    db.Inbox.Add(message);
                }
                message.Text = text; message.ChatName = CliJson.String(value, "ChatName", "chatName"); message.Sender = CliJson.String(value, "SenderJID", "senderJid");
                IncomingAttachment? attachment = null;
                if (value.TryGetProperty("Media", out var media) && media.ValueKind == JsonValueKind.Object)
                {
                    message.MediaType = CliJson.String(media, "Type");
                    if (attachments.WithinRetention(timestamp)) attachment = await attachments.Queue(db, account, chat, msgId, media);
                }
                var payload = new { messageId = msgId, chatJid = chat, chatName = message.ChatName, senderJid = message.Sender, text, fromMe = message.FromMe, timestamp, mediaType = message.MediaType, attachment = attachment is null ? null : attachments.View(attachment) };
                var type = updated ? "message.updated" : message.FromMe ? "message.sent" : "message.received";
                await webhooks.Queue(db, account.TenantId, account.Id, type, payload, $"message:{accountId}:{chat}:{msgId}:" + SecretTokens.Hash(text));
            }
            await db.SaveChangesAsync();
        }
        finally { gate.Release(); }
    }
}
