using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QRCoder;
using WaTenancy.Web.Data;
using WaTenancy.Web.Infrastructure;
using WaTenancy.Web.Security;

namespace WaTenancy.Web.Services;

public sealed record PairingView(string? QrImage, string? PairCode, DateTime? ExpiresAt, AccountState State);
public sealed class AccountSupervisor(IDbContextFactory<AppDbContext> factory, IWacliClient cli, AppPaths paths, TenantAccess access, IDataProtectionProvider protection, IOptions<AppOptions> options, WebhookService webhooks, ILogger<AccountSupervisor> logger) : BackgroundService
{
    private sealed class Worker
    {
        public ICliSession? Session;
        public bool Pairing;
        public bool IntentionalStop;
        public string? QrCode;
        public string? PairCode;
        public DateTime? ExpiresAt;
        public DateTime NextStart;
        public int Failures;
        public CancellationTokenSource? PairTimeout;
    }
    private readonly ConcurrentDictionary<Guid, Worker> workers = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private CancellationToken serverCancellation;
    public async Task<PairingView> Pairing(ClaimsPrincipal p, Guid tenantId, Guid accountId)
    {
        await access.Require(p, tenantId, TenantRole.Admin, "accounts");
        var account = await Account(tenantId, accountId);
        if (!workers.TryGetValue(accountId, out var worker) || worker.ExpiresAt <= DateTime.UtcNow) return new(null, null, null, account.State);
        string? image = null;
        if (worker.QrCode is not null)
        {
            using var data = QRCodeGenerator.GenerateQrCode(worker.QrCode, QRCodeGenerator.ECCLevel.M);
            using var code = new PngByteQRCode(data);
            image = "data:image/png;base64," + Convert.ToBase64String(code.GetGraphic(6));
        }
        return new(image, worker.PairCode, worker.ExpiresAt, account.State);
    }
    public async Task StartPairing(ClaimsPrincipal p, Guid tenantId, Guid accountId, string? phone = null)
    {
        await access.Require(p, tenantId, TenantRole.Admin, "accounts");
        if (!options.Value.EnableWorkers) throw new AppException(503, "Workers are disabled in this environment.");
        if (!string.IsNullOrWhiteSpace(phone)) phone = MessageService.Recipient(phone);
        if (phone?.Contains('@') == true) throw new AppException(400, "Phone pairing requires an international phone number.");
        await lifecycle.WaitAsync();
        try
        {
            var account = await Account(tenantId, accountId);
            if (account.State == AccountState.Connected) throw new AppException(409, "This number is already connected. Disconnect it before pairing again.");
            await StopWorker(accountId);
            if (workers.Values.Count(x => x.Session is not null) >= options.Value.MaxTotalWorkers) throw new AppException(409, "Server worker capacity has been reached.");
            await using var db = await factory.CreateDbContextAsync();
            await db.Accounts.Where(x => x.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(x => x.WatchEnabled, false).SetProperty(x => x.State, AccountState.Pairing).SetProperty(x => x.LastError, (string?)null));
            var worker = new Worker { Pairing = true, ExpiresAt = DateTime.UtcNow.AddMinutes(3) }; workers[accountId] = worker;
            List<string> args = ["--events", "--timeout", "3m", "auth", "--qr-format", "text", "--idle-exit", "5s"];
            if (!string.IsNullOrWhiteSpace(phone)) { args.Add("--phone"); args.Add(phone); }
            worker.PairTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
            worker.PairTimeout.CancelAfter(TimeSpan.FromMinutes(3));
            worker.Session = cli.Start(paths.Store(tenantId, accountId), args, evt => OnEvent(account, worker, evt), worker.PairTimeout.Token);
            TenantService.Audit(db, p, tenantId, "account.pairing_started", accountId.ToString()); await db.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is not AppException)
        {
            await SetState(accountId, AccountState.Error, "Could not start wacli. Check the configured executable and server permissions.");
            throw new AppException(503, "Could not start the WhatsApp pairing worker.");
        }
        finally { lifecycle.Release(); }
    }
    public async Task SetWatch(ClaimsPrincipal p, Guid tenantId, Guid accountId, bool watch)
    {
        await access.Require(p, tenantId, TenantRole.Admin, "accounts");
        if (!options.Value.EnableWorkers && watch) throw new AppException(503, "Workers are disabled in this environment.");
        await lifecycle.WaitAsync();
        try
        {
            var account = await Account(tenantId, accountId);
            if (workers.TryGetValue(accountId, out var current) && current.Pairing && watch) throw new AppException(409, "Finish pairing first.");
            if (watch && account.State == AccountState.NeedsPairing) throw new AppException(409, "Pair this number before starting sync.");
            if (watch && account.State != AccountState.Connected)
            {
                var result = await cli.Run(paths.Store(tenantId, accountId), ["--read-only", "--json", "auth", "status"], serverCancellation);
                if (result.ExitCode != 0 || result.TimedOut || !CliJson.Bool(CliJson.Parse(result.Output), "authenticated")) throw new AppException(409, "Pair this number before starting its watcher.");
            }
            await using var db = await factory.CreateDbContextAsync();
            await db.Accounts.Where(x => x.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(x => x.WatchEnabled, watch));
            if (!watch) { await StopWorker(accountId); await SetState(accountId, AccountState.Paused); }
            else if (workers.TryGetValue(accountId, out current)) current.NextStart = DateTime.MinValue;
            TenantService.Audit(db, p, tenantId, watch ? "account.watch_started" : "account.watch_stopped", accountId.ToString()); await db.SaveChangesAsync();
        }
        finally { lifecycle.Release(); }
    }
    public async Task Logout(ClaimsPrincipal p, Guid tenantId, Guid accountId)
    {
        await access.Require(p, tenantId, TenantRole.Admin, "accounts");
        await lifecycle.WaitAsync();
        try
        {
            await Account(tenantId, accountId); await StopWorker(accountId);
            await using var db = await factory.CreateDbContextAsync();
            await db.Accounts.Where(x => x.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(x => x.WatchEnabled, false));
            var result = await cli.Run(paths.Store(tenantId, accountId), ["--json", "auth", "logout"], serverCancellation);
            if (result.ExitCode != 0 || result.TimedOut) { await SetState(accountId, AccountState.Error, "Logout was not confirmed. Check WhatsApp's Linked devices screen."); throw new AppException(502, "WhatsApp did not confirm logout. You can unlink the device from your phone."); }
            await SetState(accountId, AccountState.NeedsPairing);
            TenantService.Audit(db, p, tenantId, "account.logged_out", accountId.ToString()); await db.SaveChangesAsync();
        }
        finally { lifecycle.Release(); }
    }
    private async Task<WhatsAppAccount> Account(Guid tenantId, Guid accountId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Accounts.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == accountId) ?? throw new AppException(404, "Account not found.");
    }
    private async Task StopWorker(Guid accountId)
    {
        if (!workers.TryRemove(accountId, out var worker)) return;
        worker.IntentionalStop = true;
        if (worker.Session is not null) await worker.Session.DisposeAsync();
        worker.PairTimeout?.Dispose();
    }
    private async Task OnEvent(WhatsAppAccount account, Worker worker, LifecycleEvent evt)
    {
        if (worker.IntentionalStop) return;
        switch (evt.Event)
        {
            case "qr_code": worker.QrCode = CliJson.String(evt.Data, "code"); worker.ExpiresAt = DateTime.UtcNow.AddSeconds(90); break;
            case "pair_code": worker.PairCode = CliJson.String(evt.Data, "code"); worker.ExpiresAt = DateTime.UtcNow.AddMinutes(3); break;
            case "connected":
                worker.QrCode = null; worker.PairCode = null; worker.ExpiresAt = null; worker.Failures = 0;
                await SetState(account.Id, worker.Pairing ? AccountState.Connecting : AccountState.Connected); break;
            case "disconnected": case "reconnecting": await SetState(account.Id, AccountState.Connecting); break;
            case "logged_out":
                await using (var db = await factory.CreateDbContextAsync()) await db.Accounts.Where(x => x.Id == account.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.WatchEnabled, false));
                worker.QrCode = null; worker.PairCode = null; await SetState(account.Id, AccountState.NeedsPairing, "The phone unlinked this session. Pair it again."); break;
            case "error": case "warning":
                // Do not persist untrusted CLI lines, QR values, or message contents in logs.
                var code = CliJson.String(evt.Data, "code");
                var message = CliJson.String(evt.Data, "message");
                if (code.Contains("limit", StringComparison.OrdinalIgnoreCase) || message.Contains("message limit", StringComparison.OrdinalIgnoreCase) || message.Contains("database size limit", StringComparison.OrdinalIgnoreCase))
                {
                    await using var db = await factory.CreateDbContextAsync();
                    await db.Accounts.Where(x => x.Id == account.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.WatchEnabled, false));
                    await SetState(account.Id, AccountState.Error, "Account storage limit reached. Review retention and limits before restarting.");
                }
                break;
        }
    }
    public async Task SetState(Guid id, AccountState state, string? error = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        var account = await db.Accounts.FindAsync(id); if (account is null) return;
        var changed = account.State != state;
        account.State = state; account.LastError = error;
        if (state == AccountState.Connected) account.LastConnectedAt = DateTime.UtcNow;
        if (changed) await webhooks.Queue(db, account.TenantId, id, "account.status", new { state = state.ToString(), name = account.Name, phone = account.Phone, error }, "status:" + Guid.NewGuid());
        await db.SaveChangesAsync();
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        serverCancellation = ct;
        await using (var startup = await factory.CreateDbContextAsync(ct))
            await startup.Accounts.Where(x => x.State == AccountState.Connected || x.State == AccountState.Pairing || x.State == AccountState.Connecting).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, AccountState.Disconnected), ct);
        if (!options.Value.EnableWorkers) return;
        while (!ct.IsCancellationRequested)
        {
            await lifecycle.WaitAsync(ct);
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                var accounts = await db.Accounts.Include(x => x.Tenant).ToListAsync(ct);
                foreach (var account in accounts)
                {
                    workers.TryGetValue(account.Id, out var worker);
                    if (!account.Tenant.IsEnabled || !account.WatchEnabled && worker is not { Pairing: true })
                    {
                        if (worker is not null) { await StopWorker(account.Id); await SetState(account.Id, AccountState.Paused); }
                        continue;
                    }
                    if (worker?.Session is not null && worker.Session.Completion.IsCompleted)
                    {
                        var exitCode = await worker.Session.Completion; await worker.Session.DisposeAsync(); worker.Session = null;
                        worker.PairTimeout?.Dispose(); worker.PairTimeout = null;
                        if (worker.Pairing)
                        {
                            worker.Pairing = false; worker.QrCode = null; worker.PairCode = null;
                            var status = await cli.Run(paths.Store(account.TenantId, account.Id), ["--read-only", "--json", "auth", "status"], ct);
                            var authenticated = status.ExitCode == 0 && CliJson.Bool(CliJson.Parse(status.Output), "authenticated");
                            if (authenticated)
                            {
                                var phone = CliJson.String(CliJson.Parse(status.Output), "phone", "linked_jid");
                                await db.Accounts.Where(x => x.Id == account.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.WatchEnabled, true).SetProperty(x => x.Phone, phone), ct);
                                account.WatchEnabled = true; worker.NextStart = DateTime.MinValue;
                            }
                            else { await SetState(account.Id, AccountState.NeedsPairing, "Pairing expired or was not completed. Generate a new QR code."); workers.TryRemove(account.Id, out _); continue; }
                        }
                        else
                        {
                            var latest = await Account(account.TenantId, account.Id);
                            if (!latest.WatchEnabled || latest.State == AccountState.NeedsPairing) { workers.TryRemove(account.Id, out _); continue; }
                            worker.Failures++; worker.NextStart = DateTime.UtcNow.AddSeconds(Math.Min(120, Math.Pow(2, Math.Min(worker.Failures, 7))));
                            await SetState(account.Id, AccountState.Error, $"Sync exited ({exitCode}). Restart scheduled in {Math.Ceiling((worker.NextStart - DateTime.UtcNow).TotalSeconds)} seconds.");
                        }
                    }
                    if (worker?.Session is not null || !account.WatchEnabled || worker?.NextStart > DateTime.UtcNow) continue;
                    if (workers.Values.Count(x => x.Session is not null) >= options.Value.MaxTotalWorkers) { await SetState(account.Id, AccountState.Error, "Server worker capacity reached."); continue; }
                    worker ??= new Worker(); workers[account.Id] = worker;
                    try
                    {
                        await SetState(account.Id, AccountState.Connecting);
                        var secret = protection.CreateProtector("WaTenancy.Secrets.v1").Unprotect(account.ProtectedWebhookSecret);
                        List<string> args = ["--events", "sync", "--follow", "--max-reconnect", "5m", "--max-messages", options.Value.MaxMessagesPerAccount.ToString(), "--max-db-size", options.Value.MaxDatabaseSize, "--send-spacing", options.Value.SendSpacingMilliseconds + "ms", "--webhook", options.Value.InternalBaseUrl.TrimEnd('/') + "/internal/wacli/" + account.Id, "--webhook-secret", secret, "--webhook-events", "message,receipt,chat_presence", "--webhook-allow-private"];
                        worker.Session = cli.Start(paths.Store(account.TenantId, account.Id), args, evt => OnEvent(account, worker, evt), ct);
                    }
                    catch (Exception)
                    {
                        worker.Failures++; worker.NextStart = DateTime.UtcNow.AddSeconds(Math.Min(120, Math.Pow(2, Math.Min(worker.Failures, 7))));
                        await SetState(account.Id, AccountState.Error, "Unable to launch wacli. Check the executable path and server permissions.");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError("Supervisor cycle failed ({ExceptionType}).", ex.GetType().Name); }
            finally { lifecycle.Release(); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { break; }
        }
        foreach (var id in workers.Keys) await StopWorker(id);
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        foreach (var id in workers.Keys) await StopWorker(id);
    }
}
