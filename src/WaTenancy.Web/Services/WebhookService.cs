using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WaTenancy.Web.Data;
using WaTenancy.Web.Infrastructure;
using WaTenancy.Web.Security;

namespace WaTenancy.Web.Services;

public sealed record DeliveryView(Guid Id, Guid? AccountId, string EventType, string State, int Attempts, int? LastStatusCode, string? LastError, DateTime CreatedAt, DateTime? DeliveredAt, DateTime NextAttemptAt);
public sealed class WebhookService(IDbContextFactory<AppDbContext> factory, TenantAccess access)
{
    public async Task Queue(AppDbContext db, Guid tenantId, Guid? accountId, string type, object data, string deduplicationKey)
    {
        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant is null || !tenant.IsEnabled || !tenant.WebhookEnabled || string.IsNullOrEmpty(tenant.WebhookUrl)) return;
        if (await db.WebhookDeliveries.AnyAsync(x => x.TenantId == tenantId && x.DeduplicationKey == deduplicationKey)) return;
        var delivery = new WebhookDelivery { TenantId = tenantId, AccountId = accountId, EventType = type, DeduplicationKey = deduplicationKey, TargetUrl = tenant.WebhookUrl, ProtectedSecret = tenant.ProtectedWebhookSecret };
        delivery.Payload = JsonSerializer.Serialize(new { version = "1", eventId = delivery.Id, eventType = type, occurredAt = DateTime.UtcNow, tenantId, accountId, data });
        db.WebhookDeliveries.Add(delivery);
    }
    public async Task<List<DeliveryView>> List(ClaimsPrincipal p, Guid id, int page = 1)
    {
        await access.Require(p, id, TenantRole.Admin);
        await using var db = await factory.CreateDbContextAsync();
        return await db.WebhookDeliveries.Where(x => x.TenantId == id).OrderByDescending(x => x.CreatedAt).Skip((Math.Clamp(page, 1, 10000) - 1) * 50).Take(50).Select(x => new DeliveryView(x.Id, x.AccountId, x.EventType, x.State, x.Attempts, x.LastStatusCode, x.LastError, x.CreatedAt, x.DeliveredAt, x.NextAttemptAt)).ToListAsync();
    }
    public async Task Retry(ClaimsPrincipal p, Guid id, Guid deliveryId)
    {
        await access.Require(p, id, TenantRole.Admin);
        await using var db = await factory.CreateDbContextAsync();
        var tenant = await db.Tenants.FindAsync(id);
        if (tenant is null || !tenant.WebhookEnabled || tenant.WebhookUrl is null) throw new AppException(409, "Enable and configure the webhook first.");
        var delivery = await db.WebhookDeliveries.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == id && x.Id == deliveryId) ?? throw new AppException(404, "Delivery not found.");
        if (delivery.State != "Failed" && !(delivery.State == "Pending" && delivery.Attempts > 0)) throw new AppException(409, "Only failed or waiting retry deliveries can be retried.");
        var claimed = await db.WebhookDeliveries.Where(x => x.TenantId == id && x.Id == deliveryId && (x.State == "Failed" || x.State == "Pending" && x.Attempts > 0))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Pending").SetProperty(x => x.Attempts, 0).SetProperty(x => x.NextAttemptAt, DateTime.UtcNow)
                .SetProperty(x => x.TargetUrl, tenant.WebhookUrl).SetProperty(x => x.ProtectedSecret, tenant.ProtectedWebhookSecret)
                .SetProperty(x => x.LastStatusCode, (int?)null).SetProperty(x => x.LastError, (string?)null));
        if (claimed == 0) throw new AppException(409, "This delivery is already being sent. Wait for the attempt to finish.");
        TenantService.Audit(db, p, id, "webhook.retried", deliveryId.ToString()); await db.SaveChangesAsync();
    }
    public async Task Test(ClaimsPrincipal p, Guid id)
    {
        await access.Require(p, id, TenantRole.Admin);
        await using var db = await factory.CreateDbContextAsync();
        var tenant = await db.Tenants.FindAsync(id);
        if (tenant is null || !tenant.WebhookEnabled) throw new AppException(409, "Enable the webhook first.");
        await Queue(db, id, null, "webhook.test", new { message = "WaTenancy webhook connection test" }, "test:" + Guid.NewGuid());
        TenantService.Audit(db, p, id, "webhook.test_queued"); await db.SaveChangesAsync();
    }
    public static string Signature(string secret, string timestamp, string payload) => "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "." + payload))).ToLowerInvariant();
}
public sealed record WebhookTransportResult(int? StatusCode, string? Error)
{
    public bool Succeeded => StatusCode is >= 200 and < 300;
    public static WebhookTransportResult Http(int code) => new(code, code is >= 200 and < 300 ? null : $"Endpoint returned HTTP {code}.");
    public static WebhookTransportResult Failed(string error) => new(null, error);
}
public interface IWebhookTransport { Task<WebhookTransportResult> Send(string url, string payload, string eventId, string secret, CancellationToken ct); }
public sealed class PublicWebhookTransport(WebhookEndpointPolicy endpoints) : IWebhookTransport
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x01 && (b[2] == 0x0d && b[3] == 0xb8 || b[2] < 2))
                && !(b[0] == 0x20 && b[1] == 0x02) && !(b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0);
        return b[0] is not (0 or 10 or 127) && b[0] < 224 && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31)
            && !(b[0] == 192 && (b[1] == 168 || b[1] == 0 || b[1] == 88 && b[2] == 99)) && !(b[0] == 100 && b[1] is >= 64 and <= 127)
            && !(b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100)) && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }
    public static async Task ValidateUrl(string url)
    {
        if (url.Length > 2048 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Port != 443)
            throw new AppException(400, "Use a public HTTPS webhook URL on port 443, without credentials or a fragment.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, timeout.Token);
            if (addresses.Length == 0 || addresses.Any(x => !IsPublic(x))) throw new AppException(400, "Webhook endpoints must resolve only to public IP addresses.");
        }
        catch (SocketException) { throw new AppException(400, "Webhook hostname could not be resolved."); }
        catch (OperationCanceledException) { throw new AppException(400, "Webhook hostname resolution timed out."); }
    }
    public async Task<WebhookTransportResult> Send(string url, string payload, string eventId, string secret, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await endpoints.Validate(url).WaitAsync(deadline.Token);
            var uri = new Uri(url.Trim());
            var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, deadline.Token);
            var localReceiver = endpoints.AllowsLocalReceiver(uri);
            if (addresses.Length == 0 || addresses.Any(x => localReceiver ? !IPAddress.IsLoopback(x) : !IsPublic(x)))
                return WebhookTransportResult.Failed("Webhook hostname resolved to a disallowed address.");

            foreach (var address in addresses)
            {
                using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, uri.Port), deadline.Token); }
                catch (SocketException) { continue; }

                // Establish and validate the connection here, within our own error boundary.
                // The HTTP callback only supplies this already connected, pinned stream.
                using var stream = new NetworkStream(socket, ownsSocket: false);
                using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, PooledConnectionLifetime = TimeSpan.Zero };
                handler.ConnectCallback = (_, _) => ValueTask.FromResult<Stream>(stream);
                using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
                request.Headers.Add("X-WaTenancy-Event-Id", eventId);
                request.Headers.Add("X-WaTenancy-Timestamp", timestamp);
                request.Headers.Add("X-WaTenancy-Signature", WebhookService.Signature(secret, timestamp, payload));
                request.Headers.UserAgent.ParseAdd("WaTenancy/1.0");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                return WebhookTransportResult.Http((int)response.StatusCode);
            }
            return WebhookTransportResult.Failed("Connection refused or webhook endpoint unavailable.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return WebhookTransportResult.Failed("Webhook request timed out."); }
        catch (HttpRequestException) { return WebhookTransportResult.Failed("Webhook HTTP or TLS connection failed."); }
        catch (SocketException) { return WebhookTransportResult.Failed("Webhook hostname could not be resolved or reached."); }
        catch (AppException ex) { return WebhookTransportResult.Failed(ex.Message); }
    }
}
public sealed class WebhookDispatcher(IDbContextFactory<AppDbContext> factory, IWebhookTransport transport, IDataProtectionProvider protection, IOptions<AppOptions> options, ILogger<WebhookDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await using (var startup = await factory.CreateDbContextAsync(ct))
            await startup.WebhookDeliveries.Where(x => x.State == "Sending").ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Pending"), ct);
        var active = new Dictionary<Guid, Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var tenantId in active.Where(x => x.Value.IsCompleted).Select(x => x.Key).ToArray()) active.Remove(tenantId);
                try
                {
                    var capacity = options.Value.MaxConcurrentWebhooks - active.Count;
                    if (capacity > 0)
                    {
                        await using var db = await factory.CreateDbContextAsync(ct);
                        var busy = active.Keys.ToArray();
                        var due = await db.WebhookDeliveries.Where(x => x.State == "Pending" && x.NextAttemptAt <= DateTime.UtcNow && x.Tenant.IsEnabled && x.Tenant.WebhookEnabled && !busy.Contains(x.TenantId))
                            .GroupBy(x => x.TenantId).Select(g => new { TenantId = g.Key, DueAt = g.Min(x => x.NextAttemptAt) }).OrderBy(x => x.DueAt).Take(capacity).ToListAsync(ct);
                        foreach (var tenant in due) active[tenant.TenantId] = Dispatch(tenant.TenantId, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError("Webhook scheduling failed ({ExceptionType}).", ex.GetType().Name); }
                try { await Task.Delay(active.Count == 0 ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(200), ct); } catch (OperationCanceledException) { break; }
            }
        }
        finally { await Task.WhenAll(active.Values); }
    }
    private async Task Dispatch(Guid tenantId, CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var delivery = await db.WebhookDeliveries.Where(x => x.TenantId == tenantId && x.State == "Pending" && x.NextAttemptAt <= DateTime.UtcNow && x.Tenant.IsEnabled && x.Tenant.WebhookEnabled).OrderBy(x => x.NextAttemptAt).ThenBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            if (delivery is null || await db.WebhookDeliveries.Where(x => x.Id == delivery.Id && x.State == "Pending").ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Sending"), ct) == 0) return;
            await db.Entry(delivery).ReloadAsync(ct);
            delivery.Attempts++;
            delivery.LastStatusCode = null;
            try
            {
                var secret = protection.CreateProtector("WaTenancy.Secrets.v1").Unprotect(delivery.ProtectedSecret);
                var result = await transport.Send(delivery.TargetUrl, delivery.Payload, delivery.Id.ToString(), secret, ct);
                delivery.LastStatusCode = result.StatusCode;
                if (result.Succeeded) { delivery.State = "Delivered"; delivery.DeliveredAt = DateTime.UtcNow; delivery.LastError = null; }
                else { delivery.LastError = result.Error ?? "Webhook delivery failed."; Fail(delivery); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { delivery.State = "Pending"; await db.SaveChangesAsync(CancellationToken.None); return; }
            catch (Exception ex) { delivery.LastError = ex is AppException a ? a.Message : "Endpoint could not be reached or the request timed out."; Fail(delivery); }
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError("Webhook dispatch failed for tenant {TenantId} ({ExceptionType}).", tenantId, ex.GetType().Name); }
    }
    private static void Fail(WebhookDelivery d)
    {
        d.State = d.Attempts >= 8 ? "Failed" : "Pending";
        d.NextAttemptAt = DateTime.UtcNow.AddSeconds(Math.Min(3600, 10 * Math.Pow(2, d.Attempts - 1)));
    }
}
