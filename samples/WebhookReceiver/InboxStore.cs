using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

public sealed class InboxStore(string connectionString)
{
    public async Task Initialize()
    {
        await using var db = await Open(); await using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS messages(tenant_id TEXT NOT NULL,account_id TEXT NOT NULL,chat_jid TEXT NOT NULL,message_id TEXT NOT NULL,chat_name TEXT NOT NULL,sender_jid TEXT NOT NULL,text TEXT NOT NULL,from_me INTEGER NOT NULL,media_type TEXT NOT NULL,sent_at TEXT NOT NULL,revision_at TEXT NOT NULL,deleted INTEGER NOT NULL DEFAULT 0,event_id TEXT NOT NULL,PRIMARY KEY(tenant_id,account_id,chat_jid,message_id));
            CREATE INDEX IF NOT EXISTS ix_messages_thread ON messages(tenant_id,account_id,chat_jid,sent_at);
            CREATE TABLE IF NOT EXISTS replies(job_id TEXT PRIMARY KEY,tenant_id TEXT NOT NULL,account_id TEXT NOT NULL,chat_jid TEXT NOT NULL,text TEXT NOT NULL,state TEXT NOT NULL,created_at TEXT NOT NULL,message_id TEXT NULL,error TEXT NULL);
            CREATE TABLE IF NOT EXISTS inbox_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS attachments(id TEXT PRIMARY KEY,tenant_id TEXT NOT NULL,account_id TEXT NOT NULL,chat_jid TEXT NOT NULL,message_id TEXT NOT NULL,body TEXT NOT NULL,state TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_attachments_message ON attachments(tenant_id,account_id,chat_jid,message_id);
            """;
        await command.ExecuteNonQueryAsync(); command.CommandText = "SELECT value FROM inbox_meta WHERE key='projection_version'";
        if ((string?)await command.ExecuteScalarAsync() == "2") return;
        long cursor = 0;
        while (true)
        {
            command.CommandText = "SELECT rowid,body FROM events WHERE rowid>$cursor ORDER BY rowid LIMIT 500";
            command.Parameters.Clear(); command.Parameters.AddWithValue("$cursor", cursor);
            var batch = new List<(long Id,string Body)>();
            await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) batch.Add((reader.GetInt64(0),reader.GetString(1)));
            if (batch.Count == 0) break;
            foreach (var row in batch) { using var json = JsonDocument.Parse(row.Body); await Project(db, null, json.RootElement); cursor = row.Id; }
        }
        command.Parameters.Clear(); command.CommandText = "INSERT OR REPLACE INTO inbox_meta VALUES('projection_version','2')"; await command.ExecuteNonQueryAsync();
    }
    public static string Text(JsonElement value,string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key,out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    public static async Task Project(SqliteConnection db,SqliteTransaction? transaction,JsonElement envelope)
    {
        var type = Text(envelope,"eventType");
        if (!envelope.TryGetProperty("data",out var data) || data.ValueKind != JsonValueKind.Object) return;
        var tenant = Text(envelope,"tenantId"); var account = Text(envelope,"accountId");
        if (!Guid.TryParse(tenant,out _) || !Guid.TryParse(account,out _)) return;
        if (data.TryGetProperty("attachment",out var attachment) && attachment.ValueKind==JsonValueKind.Object && Guid.TryParse(Text(attachment,"id"),out var attachmentId))
        {
            await using var media=db.CreateCommand(); media.Transaction=transaction;
            media.CommandText="INSERT INTO attachments VALUES($id,$tenant,$account,$chat,$message,$body,$state) ON CONFLICT(id) DO UPDATE SET body=excluded.body,state=excluded.state WHERE attachments.tenant_id=excluded.tenant_id AND attachments.account_id=excluded.account_id AND (attachments.state!='Ready' OR excluded.state='Ready')";
            media.Parameters.AddWithValue("$id",attachmentId.ToString()); media.Parameters.AddWithValue("$tenant",tenant);media.Parameters.AddWithValue("$account",account);media.Parameters.AddWithValue("$chat",Text(data,"chatJid"));media.Parameters.AddWithValue("$message",Text(data,"messageId"));media.Parameters.AddWithValue("$body",attachment.GetRawText());media.Parameters.AddWithValue("$state",Text(attachment,"state"));
            await media.ExecuteNonQueryAsync();
        }
        if(type is "message.media.ready" or "message.media.failed") return;
        await using var command = db.CreateCommand(); command.Transaction = transaction;
        if (type == "message.dispatch")
        {
            command.CommandText = "UPDATE replies SET state=$state,message_id=$message,error=$error WHERE job_id=$job AND tenant_id=$tenant AND account_id=$account";
            command.Parameters.AddWithValue("$state",Text(data,"state")); command.Parameters.AddWithValue("$message",Text(data,"messageId")); command.Parameters.AddWithValue("$error",Text(data,"error")); command.Parameters.AddWithValue("$job",Text(data,"jobId"));
        }
        else
        {
            if (type is not ("message.received" or "message.sent" or "message.updated" or "message.deleted")) return;
            var chat = Text(data,"chatJid"); var id = Text(data,"messageId"); if (chat.Length is < 1 or > 200 || id.Length is < 1 or > 200) return;
            var revision = Timestamp(Text(envelope,"occurredAt")); var sent = Timestamp(Text(data,"timestamp"),revision);
            command.CommandText = """
                INSERT INTO messages(tenant_id,account_id,chat_jid,message_id,chat_name,sender_jid,text,from_me,media_type,sent_at,revision_at,deleted,event_id)
                VALUES($tenant,$account,$chat,$id,$name,$sender,$text,$from,$media,$at,$revision,$deleted,$event)
                ON CONFLICT(tenant_id,account_id,chat_jid,message_id) DO UPDATE SET
                chat_name=CASE WHEN excluded.chat_name='' THEN messages.chat_name ELSE excluded.chat_name END,
                sender_jid=CASE WHEN excluded.sender_jid='' THEN messages.sender_jid ELSE excluded.sender_jid END,
                text=excluded.text,from_me=CASE WHEN excluded.deleted=1 THEN messages.from_me ELSE excluded.from_me END,
                media_type=excluded.media_type,revision_at=excluded.revision_at,deleted=excluded.deleted,event_id=excluded.event_id
                WHERE excluded.revision_at>=messages.revision_at
                """;
            command.Parameters.AddWithValue("$chat",chat); command.Parameters.AddWithValue("$id",id); command.Parameters.AddWithValue("$name",Text(data,"chatName")); command.Parameters.AddWithValue("$sender",Text(data,"senderJid"));
            command.Parameters.AddWithValue("$text",type == "message.deleted" ? "" : Text(data,"text"));
            command.Parameters.AddWithValue("$from",data.TryGetProperty("fromMe",out var from) && from.ValueKind == JsonValueKind.True ? 1 : 0);
            command.Parameters.AddWithValue("$media",Text(data,"mediaType")); command.Parameters.AddWithValue("$at",sent); command.Parameters.AddWithValue("$revision",revision);
            command.Parameters.AddWithValue("$deleted",type == "message.deleted" ? 1 : 0); command.Parameters.AddWithValue("$event",Text(envelope,"eventId"));
        }
        command.Parameters.AddWithValue("$tenant",tenant); command.Parameters.AddWithValue("$account",account);
        await command.ExecuteNonQueryAsync();
        if(type=="message.deleted")
        {
            command.Parameters.Clear();command.CommandText="DELETE FROM attachments WHERE tenant_id=$tenant AND account_id=$account AND chat_jid=$chat AND message_id=$message";
            command.Parameters.AddWithValue("$tenant",tenant);command.Parameters.AddWithValue("$account",account);command.Parameters.AddWithValue("$chat",Text(data,"chatJid"));command.Parameters.AddWithValue("$message",Text(data,"messageId"));await command.ExecuteNonQueryAsync();
        }
    }
    private static string Timestamp(string value,string? fallback=null) => DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var at) ? at.UtcDateTime.ToString("O") : fallback ?? DateTime.UtcNow.ToString("O");
    private async Task<SqliteConnection> Open() { var db = new SqliteConnection(connectionString); await db.OpenAsync(); return db; }
    public async Task<object> Info()
    {
        await using var db = await Open(); await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT (SELECT count(*) FROM events),(SELECT count(*) FROM messages WHERE deleted=0),(SELECT count(*) FROM (SELECT 1 FROM messages GROUP BY tenant_id,account_id,chat_jid)),(SELECT max(received_at) FROM events)";
        await using var r = await cmd.ExecuteReaderAsync(); await r.ReadAsync(); return new { events=r.GetInt64(0),messages=r.GetInt64(1),threads=r.GetInt64(2),lastEvent=r.IsDBNull(3)?null:r.GetString(3) };
    }
    public async Task<List<object>> Threads(string? search,int page)
    {
        await using var db = await Open(); await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            WITH ranked AS (SELECT *,row_number() OVER(PARTITION BY tenant_id,account_id,chat_jid ORDER BY sent_at DESC,message_id DESC) AS rank FROM messages),
            threads AS (SELECT r.*,coalesce((SELECT nullif(m.chat_name,'') FROM messages m WHERE m.tenant_id=r.tenant_id AND m.account_id=r.account_id AND m.chat_jid=r.chat_jid AND m.chat_name<>'' ORDER BY m.sent_at DESC LIMIT 1),r.chat_jid) AS title FROM ranked r WHERE rank=1)
            SELECT tenant_id,account_id,chat_jid,title,text,from_me,media_type,sent_at,deleted FROM threads
            WHERE $q='' OR title LIKE $q ESCAPE '\' OR chat_jid LIKE $q ESCAPE '\' OR EXISTS(SELECT 1 FROM messages m WHERE m.tenant_id=threads.tenant_id AND m.account_id=threads.account_id AND m.chat_jid=threads.chat_jid AND m.text LIKE $q ESCAPE '\')
            ORDER BY sent_at DESC LIMIT 51 OFFSET $offset
            """;
        cmd.Parameters.AddWithValue("$q",string.IsNullOrWhiteSpace(search)?"":"%"+search.Trim().Replace("\\","\\\\").Replace("%","\\%").Replace("_","\\_")+"%"); cmd.Parameters.AddWithValue("$offset",Math.Clamp(page,0,10000)*50);
        var rows = new List<object>(); await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add(new { tenantId=r.GetString(0),accountId=r.GetString(1),chatJid=r.GetString(2),name=r.GetString(3),preview=r.GetString(4),fromMe=r.GetBoolean(5),mediaType=r.GetString(6),timestamp=r.GetString(7),deleted=r.GetBoolean(8) }); return rows;
    }
    public async Task<object> Messages(Guid tenant,Guid account,string chat,int take)
    {
        await using var db = await Open(); await using var cmd = db.CreateCommand(); BindThread(cmd,tenant,account,chat);
        cmd.CommandText = "SELECT count(*) FROM messages WHERE tenant_id=$tenant AND account_id=$account AND chat_jid=$chat"; var total = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        cmd.CommandText = "SELECT message_id,text,from_me,sender_jid,media_type,sent_at,deleted,event_id,(SELECT a.body FROM attachments a WHERE a.tenant_id=m.tenant_id AND a.account_id=m.account_id AND a.chat_jid=m.chat_jid AND a.message_id=m.message_id LIMIT 1) FROM messages m WHERE tenant_id=$tenant AND account_id=$account AND chat_jid=$chat ORDER BY sent_at DESC,message_id DESC LIMIT $take";
        cmd.Parameters.AddWithValue("$take",Math.Clamp(take,50,500)); var rows = new List<object>();
        await using (var r = await cmd.ExecuteReaderAsync()) while (await r.ReadAsync()) rows.Add(new { id=r.GetString(0),text=r.GetString(1),fromMe=r.GetBoolean(2),sender=r.GetString(3),mediaType=r.GetString(4),timestamp=r.GetString(5),deleted=r.GetBoolean(6),eventId=r.GetString(7),state="",error=(string?)null,attachment=r.IsDBNull(8)||r.GetBoolean(6)?(JsonElement?)null:JsonSerializer.Deserialize<JsonElement>(r.GetString(8)) });
        rows.Reverse(); cmd.Parameters.RemoveAt("$take");
        cmd.CommandText = "SELECT job_id,text,state,created_at,error FROM replies r WHERE tenant_id=$tenant AND account_id=$account AND chat_jid=$chat AND NOT EXISTS(SELECT 1 FROM messages m WHERE m.tenant_id=r.tenant_id AND m.account_id=r.account_id AND m.chat_jid=r.chat_jid AND m.message_id=r.message_id) ORDER BY created_at";
        await using (var r = await cmd.ExecuteReaderAsync()) while (await r.ReadAsync()) rows.Add(new { id=r.GetString(0),text=r.GetString(1),fromMe=true,sender="",mediaType="",timestamp=r.GetString(3),deleted=false,eventId="",state=r.GetString(2),error=r.IsDBNull(4)?null:r.GetString(4) });
        return new { items=rows,total,hasMore=total>take };
    }
    public async Task<Guid?> AttachmentTenant(Guid id)
    {
        await using var db=await Open(); await using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT a.tenant_id FROM attachments a JOIN messages m ON m.tenant_id=a.tenant_id AND m.account_id=a.account_id AND m.chat_jid=a.chat_jid AND m.message_id=a.message_id WHERE a.id=$id AND a.state='Ready' AND m.deleted=0";
        cmd.Parameters.AddWithValue("$id",id.ToString());return Guid.TryParse((string?)await cmd.ExecuteScalarAsync(),out var tenant)?tenant:null;
    }
    public async Task<bool> HasThread(Guid tenant,Guid account,string chat)
    {
        await using var db = await Open(); await using var cmd = db.CreateCommand(); BindThread(cmd,tenant,account,chat);
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM messages WHERE tenant_id=$tenant AND account_id=$account AND chat_jid=$chat)"; return Convert.ToInt32(await cmd.ExecuteScalarAsync())==1;
    }
    public async Task SaveReply(Guid tenant,Guid account,string chat,string text,JsonElement job)
    {
        await using var db = await Open(); await using var cmd = db.CreateCommand(); BindThread(cmd,tenant,account,chat);
        cmd.CommandText = "INSERT OR IGNORE INTO replies(job_id,tenant_id,account_id,chat_jid,text,state,created_at,message_id,error) VALUES($job,$tenant,$account,$chat,$text,$state,$at,$message,$error)";
        cmd.Parameters.AddWithValue("$job",Text(job,"id")); cmd.Parameters.AddWithValue("$text",text); cmd.Parameters.AddWithValue("$state",Text(job,"state")); cmd.Parameters.AddWithValue("$at",Timestamp(Text(job,"createdAt"))); cmd.Parameters.AddWithValue("$message",Text(job,"whatsAppMessageId")); cmd.Parameters.AddWithValue("$error",Text(job,"error")); await cmd.ExecuteNonQueryAsync();
    }
    public async Task UpdateReplies(JsonElement jobs)
    {
        if (jobs.ValueKind!=JsonValueKind.Array) return;
        await using var db = await Open(); foreach (var job in jobs.EnumerateArray())
        {
            await using var cmd=db.CreateCommand(); cmd.CommandText="UPDATE replies SET state=$state,message_id=$message,error=$error WHERE job_id=$job";
            cmd.Parameters.AddWithValue("$job",Text(job,"id")); cmd.Parameters.AddWithValue("$state",Text(job,"state")); cmd.Parameters.AddWithValue("$message",Text(job,"whatsAppMessageId")); cmd.Parameters.AddWithValue("$error",Text(job,"error")); await cmd.ExecuteNonQueryAsync();
        }
    }
    public async Task<bool> HasActiveReplies()
    {
        await using var db=await Open(); await using var cmd=db.CreateCommand(); cmd.CommandText="SELECT EXISTS(SELECT 1 FROM replies WHERE state IN ('Queued','Dispatching','Sent','Delivered'))"; return Convert.ToInt32(await cmd.ExecuteScalarAsync())==1;
    }
    public async Task<bool> HasReply(Guid tenant,Guid account,Guid job)
    {
        await using var db=await Open();await using var cmd=db.CreateCommand();cmd.CommandText="SELECT EXISTS(SELECT 1 FROM replies WHERE tenant_id=$tenant AND account_id=$account AND job_id=$job)";cmd.Parameters.AddWithValue("$tenant",tenant.ToString());cmd.Parameters.AddWithValue("$account",account.ToString());cmd.Parameters.AddWithValue("$job",job.ToString());return Convert.ToInt32(await cmd.ExecuteScalarAsync())==1;
    }
    public async Task<List<object>> Events()
    {
        await using var db=await Open(); await using var cmd=db.CreateCommand(); cmd.CommandText="SELECT event_id,event_type,tenant_id,received_at FROM events ORDER BY rowid DESC LIMIT 50";
        var rows=new List<object>(); await using var r=await cmd.ExecuteReaderAsync(); while(await r.ReadAsync()) rows.Add(new { id=r.GetString(0),type=r.GetString(1),tenantId=r.GetString(2),timestamp=r.GetString(3) }); return rows;
    }
    public async Task<string?> Event(Guid id)
    {
        await using var db=await Open(); await using var cmd=db.CreateCommand(); cmd.CommandText="SELECT body FROM events WHERE event_id=$id"; cmd.Parameters.AddWithValue("$id",id.ToString()); return await cmd.ExecuteScalarAsync() as string;
    }
    private static void BindThread(SqliteCommand cmd,Guid tenant,Guid account,string chat) { cmd.Parameters.AddWithValue("$tenant",tenant.ToString());cmd.Parameters.AddWithValue("$account",account.ToString());cmd.Parameters.AddWithValue("$chat",chat); }
}
