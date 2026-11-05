using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using WaTenancy.Web.Data;
using WaTenancy.Web.Infrastructure;

namespace WaTenancy.Web.Services;

public sealed class MessageDispatcher(IDbContextFactory<AppDbContext> factory, IWacliClient cli, AppPaths paths, IOptions<AppOptions> options, WebhookService webhooks, ILogger<MessageDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await using (var db = await factory.CreateDbContextAsync(ct))
            await db.Jobs.Where(x => x.State == JobState.Dispatching).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, JobState.Unknown).SetProperty(x => x.Error, "Server restarted during dispatch. Verify delivery before sending again."), ct);
        if (!options.Value.EnableWorkers) return;
        var active=new Dictionary<Guid,Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach(var account in active.Where(x=>x.Value.IsCompleted).Select(x=>x.Key).ToArray()) active.Remove(account);
                try
                {
                    var capacity=options.Value.MaxConcurrentSends-active.Count;
                    if(capacity>0)
                    {
                        await using var db = await factory.CreateDbContextAsync(ct); var busy=active.Keys.ToArray(); var now=DateTime.UtcNow;
                        var accountIds = await db.Jobs.Where(x => x.State == JobState.Queued && x.NextAttemptAt<=now && !busy.Contains(x.AccountId) && x.Account.WatchEnabled && x.Account.State == AccountState.Connected && x.Account.Tenant.IsEnabled
                            && !db.Jobs.Any(older=>older.AccountId==x.AccountId && older.State==JobState.Queued && older.CreatedAt<x.CreatedAt))
                            .GroupBy(x => x.AccountId).Select(g => new { AccountId = g.Key, Oldest = g.Min(x => x.CreatedAt) }).OrderBy(x => x.Oldest)
                            .Take(capacity).Select(x => x.AccountId).ToListAsync(ct);
                        foreach (var accountId in accountIds)
                        {
                            var first = await db.Jobs.AsNoTracking().Where(x => x.AccountId == accountId && x.State == JobState.Queued).OrderBy(x => x.CreatedAt).ThenBy(x=>x.Id).FirstOrDefaultAsync(ct);
                            if (first is not null && first.NextAttemptAt<=now) active[accountId]=DispatchSafely(first,ct);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError("Message scheduling failed ({ExceptionType}).", ex.GetType().Name); }
                try { await Task.Delay(active.Count==0?TimeSpan.FromSeconds(1):TimeSpan.FromMilliseconds(200), ct); } catch (OperationCanceledException) { break; }
            }
        }
        finally { await Task.WhenAll(active.Values); }
    }
    private async Task DispatchSafely(MessageJob job,CancellationToken ct)
    {
        try { await Dispatch(job,ct); }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
        catch(Exception ex) { logger.LogError("Message dispatch failed for account {AccountId} ({ExceptionType}).",job.AccountId,ex.GetType().Name); }
    }
    private async Task Dispatch(MessageJob job, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Jobs.Where(x => x.Id == job.Id && x.State == JobState.Queued && x.Account.WatchEnabled && x.Account.State == AccountState.Connected && x.Account.Tenant.IsEnabled).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, JobState.Dispatching), ct) == 0) return;
        var target = await db.Jobs.FindAsync([job.Id], ct); if (target is null) return;
        target.Attempts++; target.Error=null;
        try
        {
            List<string> args = ["--json", "--lock-wait", "5s", "--timeout", options.Value.CommandTimeoutSeconds + "s", "send"];
            if (job.FileId is null) args.AddRange(["text", "--to", job.Recipient, "--message", job.Text]);
            else
            {
                var file = await db.Files.SingleOrDefaultAsync(x => x.TenantId == job.TenantId && x.Id == job.FileId, ct);
                if (file is null || !File.Exists(paths.FilePath(job.TenantId, file.Id))) throw new FileNotFoundException();
                args.AddRange(["file", "--to", job.Recipient, "--file", paths.FilePath(job.TenantId, file.Id), "--filename", file.Name, "--mime", file.ContentType, "--caption", job.Text]);
            }
            var result = await cli.Run(paths.Store(job.TenantId, job.AccountId), args, ct);
            if (result.TimedOut)
            {
                target.State = JobState.Unknown; target.Error = "Dispatch timed out. The message may have been sent. Verify it before sending again.";
            }
            else if (result.ExitCode == 0)
            {
                var json = CliJson.Parse(result.Output);
                var id = CliJson.String(json, "id", "message_id");
                if (!CliJson.Bool(json, "sent") || string.IsNullOrWhiteSpace(id)) { target.State = JobState.Unknown; target.Error = "The command finished without a confirmed message ID. Verify delivery."; }
                else
                {
                    target.WhatsAppMessageId = id;
                    var receipt = await db.Receipts.FindAsync([job.AccountId, id], ct);
                    target.State = receipt?.State ?? JobState.Sent;
                }
            }
            else
            {
                var error=result.Error;
                try { using var json=JsonDocument.Parse(result.Output); error+=" "+CliJson.String(json.RootElement,"error"); } catch(JsonException) { }
                if(error.Contains("not supported",StringComparison.OrdinalIgnoreCase)) { target.State=JobState.Failed;target.Error="The configured wacli version does not support this send operation."; }
                else if(error.Contains("store is locked",StringComparison.OrdinalIgnoreCase) || error.Contains("store lock",StringComparison.OrdinalIgnoreCase)) Retry(target,"The account store is busy while its watcher starts.");
                else if(error.Contains("not authenticated",StringComparison.OrdinalIgnoreCase)) Retry(target,"This number needs authentication before messages can be sent.");
                else if(error.Contains("it was not sent",StringComparison.OrdinalIgnoreCase)) Retry(target,"wacli confirmed that the operation was not sent.");
                else { target.State=JobState.Unknown;target.Error="wacli reported a send error. Delivery is uncertain; verify it before sending again."; }
            }
        }
        catch (FileNotFoundException) { target.State = JobState.Failed; target.Error = "The uploaded file is no longer available."; }
        catch (System.ComponentModel.Win32Exception) { Retry(target,"Unable to start the configured wacli executable."); }
        catch (Exception) { target.State = JobState.Unknown; target.Error = "Dispatch was interrupted. Verify delivery before sending again."; }
        target.CompletedAt = target.State==JobState.Queued?null:DateTime.UtcNow;
        await webhooks.Queue(db, job.TenantId, job.AccountId, "message.dispatch", new { jobId = job.Id, messageId = target.WhatsAppMessageId, state = target.State.ToString(), error = target.Error,attempts=target.Attempts,nextAttemptAt=target.NextAttemptAt }, "dispatch:" + job.Id+":"+Guid.NewGuid());
        await db.SaveChangesAsync(CancellationToken.None);
    }
    private void Retry(MessageJob job,string reason)
    {
        if(job.Attempts>=options.Value.MaxSendAttempts) {job.State=JobState.Failed;job.Error=$"Stopped after {job.Attempts} attempts. {reason}";return;}
        job.State=JobState.Queued;job.Error="Queued for retry. "+reason;
        job.NextAttemptAt=DateTime.UtcNow.AddSeconds(Math.Min(300,5*Math.Pow(2,job.Attempts-1)));
    }
}
