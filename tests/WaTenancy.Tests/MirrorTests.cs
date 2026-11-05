using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaTenancy.Web.Data;
using WaTenancy.Web.Infrastructure;
using WaTenancy.Web.Services;
namespace WaTenancy.Tests;
public sealed class MirrorTests
{
    [Fact] public async Task MirrorRecoversMissedMessagesWithoutDuplicatesAndPurgesDeletedContent()
    {
        await using var app = new TestApplication(); var seed = await app.Seed(); await app.ConfigureHook(seed.TenantA);
        var paths = app.Services.GetRequiredService<AppPaths>(); var store = paths.Store(seed.TenantA, seed.AccountA); Directory.CreateDirectory(store);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(store, "wacli.db"), Pooling = false }.ToString();
        await using var mirror = new SqliteConnection(connectionString); await mirror.OpenAsync();
        await using (var command = mirror.CreateCommand())
        {
            command.CommandText = "CREATE TABLE messages(rowid INTEGER PRIMARY KEY,chat_jid TEXT,msg_id TEXT,chat_name TEXT,sender_jid TEXT,ts INTEGER,display_text TEXT,text TEXT,from_me INTEGER,deleted_at INTEGER,payload_purged_at INTEGER,media_type TEXT,mime_type TEXT,filename TEXT,file_length INTEGER); CREATE TABLE message_payload_purges(chat_jid TEXT,msg_id TEXT); INSERT INTO messages VALUES(1,'27821234567@s.whatsapp.net','MISSED','Customer','27821234567@s.whatsapp.net',$ts,'missed customer message',NULL,0,NULL,NULL,'audio','audio/ogg','voice.ogg',8);";
            command.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()); await command.ExecuteNonQueryAsync();
        }
        var factory = app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var reconciler = new MirrorReconciler(factory, paths, app.Services.GetRequiredService<IncomingEvents>(), app.Services.GetRequiredService<WebhookService>(), app.Services.GetRequiredService<IOptions<AppOptions>>(), app.Services.GetRequiredService<ILogger<MirrorReconciler>>());
        await using var db = await factory.CreateDbContextAsync(); var account = (await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == seed.AccountA));
        await reconciler.Reconcile(account, CancellationToken.None); await reconciler.Reconcile(account, CancellationToken.None);
        Assert.Single(await db.Inbox.Where(x => x.AccountId == seed.AccountA).ToListAsync()); Assert.Equal(1, await db.WebhookDeliveries.CountAsync(x => x.EventType == "message.received"));
        var attachment=Assert.Single(await db.Attachments.ToListAsync());
        var audioPath=paths.AttachmentPath(seed.TenantA,attachment.Id);Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);await File.WriteAllTextAsync(audioPath,"fixture");
        await using (var command = mirror.CreateCommand()) { command.CommandText = "DELETE FROM messages; INSERT INTO message_payload_purges VALUES('27821234567@s.whatsapp.net','MISSED');"; await command.ExecuteNonQueryAsync(); }
        await reconciler.Reconcile(account, CancellationToken.None);
        Assert.Empty(await db.Inbox.Where(x => x.AccountId == seed.AccountA).ToListAsync()); Assert.Equal(1, await db.WebhookDeliveries.CountAsync(x => x.EventType == "message.deleted"));
        Assert.False(File.Exists(audioPath));Assert.Equal("Purged",(await db.Attachments.AsNoTracking().SingleAsync()).State);
        Assert.Equal("{}", (await db.WebhookDeliveries.AsNoTracking().SingleAsync(x => x.EventType == "message.received")).Payload);
    }
}
