using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WaTenancy.Web.Data;
using WaTenancy.Web.Infrastructure;
using WaTenancy.Web.Security;
using WaTenancy.Web.Services;

namespace WaTenancy.Tests;

public sealed class TestApplication(bool workers = false, bool realWebhooks = false, string[]? localWebhookUrls = null) : WebApplicationFactory<Program>
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "WaTenancyTests", Guid.NewGuid().ToString("N"));
    public FakeWacli Cli { get; } = new();
    public FakeTransport Transport { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(realWebhooks ? "Development" : "Testing");
        builder.ConfigureAppConfiguration((_, c) =>
        {
            var settings = new Dictionary<string, string?> { ["WaTenancy:DataRoot"] = Root, ["WaTenancy:EnableWorkers"] = workers.ToString(), ["AllowedHosts"] = "*", ["Logging:LogLevel:Default"] = "Error" };
            for (var i = 0; i < (localWebhookUrls?.Length ?? 0); i++) settings[$"WaTenancy:DevelopmentWebhookUrls:{i}"] = localWebhookUrls![i];
            c.AddInMemoryCollection(settings);
        });
        builder.ConfigureServices(s => { s.RemoveAll<IWacliClient>(); s.AddSingleton<IWacliClient>(Cli); if (!realWebhooks) { s.RemoveAll<IWebhookTransport>(); s.AddSingleton<IWebhookTransport>(Transport); } });
    }
    public async Task<SeedData> Seed()
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = new ApplicationUser { UserName = "admin@example.test", Email = "admin@example.test", IsPlatformAdmin = true, EmailConfirmed = true };
        var viewer = new ApplicationUser { UserName = "viewer@example.test", Email = "viewer@example.test", EmailConfirmed = true };
        Assert.True((await users.CreateAsync(admin, "TestPassword!2026")).Succeeded);
        Assert.True((await users.CreateAsync(viewer, "TestPassword!2026")).Succeeded);
        var principal = Principal(admin);
        var service = Services.GetRequiredService<TenantService>();
        var a = await service.CreateCompany(principal, "Alpha Company", "alpha");
        var b = await service.CreateCompany(principal, "Beta Company", "beta");
        var accountA = await service.CreateAccount(principal, a.Id, "Alpha Sales");
        var accountB = await service.CreateAccount(principal, b.Id, "Beta Sales");
        await using var db = await Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        db.Memberships.Add(new Membership { TenantId = a.Id, UserId = viewer.Id, Role = TenantRole.Viewer });
        await db.SaveChangesAsync();
        var keyA = await service.CreateKey(principal, a.Id, "Alpha API", true, true, 90);
        var keyB = await service.CreateKey(principal, b.Id, "Beta API", true, true, 90);
        return new(a.Id, b.Id, accountA.Id, accountB.Id, keyA, keyB, principal, Principal(viewer));
    }
    public static ClaimsPrincipal Principal(ApplicationUser user) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.Email!), new Claim("security_stamp", user.SecurityStamp!)], "test"));
    public HttpClient Api(string key) { var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }); client.DefaultRequestHeaders.Add("X-Api-Key", key); return client; }
    public async Task Connect(Guid id)
    {
        await using var db = await Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        await db.Accounts.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, AccountState.Connected).SetProperty(x => x.WatchEnabled, true));
    }
    public async Task ConfigureHook(Guid tenantId)
    {
        await using var db = await Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        await db.Tenants.Where(x => x.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(x => x.WebhookEnabled, true).SetProperty(x => x.WebhookUrl, "https://example.com/events"));
    }
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // Only our generated temporary root is removed; production stores are never touched.
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WaTenancyTests")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(Root).StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}
public sealed record SeedData(Guid TenantA, Guid TenantB, Guid AccountA, Guid AccountB, string KeyA, string KeyB, ClaimsPrincipal Admin, ClaimsPrincipal Viewer);
public sealed class FakeTransport : IWebhookTransport
{
    public ConcurrentQueue<(string Url, string Payload, string Id, string Secret)> Requests { get; } = new();
    public int Status { get; set; } = 200;
    public Func<string, CancellationToken, Task<int>>? Handler { get; set; }
    public async Task<WebhookTransportResult> Send(string url, string payload, string eventId, string secret, CancellationToken ct) { Requests.Enqueue((url, payload, eventId, secret)); return WebhookTransportResult.Http(Handler is null ? Status : await Handler(payload, ct)); }
}
public sealed class FakeWacli : IWacliClient
{
    public Func<string,IReadOnlyList<string>,CancellationToken,Task<CliResult>>? MediaHandler { get; set; }
    public Func<string,IReadOnlyList<string>,CancellationToken,Task<CliResult>>? SendHandler { get; set; }
    public ConcurrentDictionary<string, FakeSession> Sessions { get; } = new();
    public ConcurrentQueue<string[]> Commands { get; } = new();
    public CliResult SendResult { get; set; } = new(0, "{\"success\":true,\"data\":{\"sent\":true,\"id\":\"FAKE-ID\"},\"error\":null}", "");
    public Task<CliResult> Run(string store, IReadOnlyList<string> args, CancellationToken ct)
    {
        Commands.Enqueue(args.ToArray());
        if(args.Contains("media") && MediaHandler is not null) return MediaHandler(store,args,ct);
        if (args.Contains("status")) return Task.FromResult(new CliResult(0, "{\"success\":true,\"data\":{\"authenticated\":true,\"phone\":\"27821234567\"}}", ""));
        if (args.Contains("logout")) return Task.FromResult(new CliResult(0, "{\"success\":true,\"data\":{\"logged_out\":true}}", ""));
        if(args.Contains("send") && SendHandler is not null) return SendHandler(store,args,ct);
        return Task.FromResult(SendResult);
    }
    public ICliSession Start(string store, IReadOnlyList<string> args, Func<LifecycleEvent, Task> onEvent, CancellationToken ct)
    {
        Commands.Enqueue(args.ToArray()); var session = new FakeSession(onEvent, ct); Sessions[store] = session;
        _ = session.Emit(args.Contains("auth") ? "qr_code" : "connected", args.Contains("auth") ? new { code = "TEST-QR-PAYLOAD" } : new { code = "" }); return session;
    }
}
public sealed class FakeSession(Func<LifecycleEvent, Task> callback, CancellationToken ct) : ICliSession
{
    private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration registration = ct.Register(() => { });
    public Task<int> Completion => completion.Task;
    public Task Emit(string type, object data) => callback(new(type, JsonSerializer.SerializeToElement(data)));
    public void Exit(int code) => completion.TrySetResult(code);
    public Task Stop() { completion.TrySetResult(0); return Task.CompletedTask; }
    public async ValueTask DisposeAsync() { await Stop(); registration.Dispose(); }
}
