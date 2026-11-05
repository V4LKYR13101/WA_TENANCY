using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Text.Json;
using WaTenancy.Web.Infrastructure;
using WaTenancy.Web.Services;
using WaTenancy.Web.Security;

namespace WaTenancy.Tests;

public sealed class LocalWebhookTests
{
    [Fact]
    public async Task RealUnavailableTenantEndpointDoesNotInterruptAnotherTenantOrHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var receiver = builder.Build();
        var received = new ConcurrentQueue<Guid>();
        receiver.MapPost("/whatsapp/events", async (HttpContext context) =>
        {
            using var json = await JsonDocument.ParseAsync(context.Request.Body);
            received.Enqueue(json.RootElement.GetProperty("tenantId").GetGuid());
            return Results.Ok();
        });
        await receiver.StartAsync();
        try
        {
            // Reserve an endpoint without listening, so connections really are refused.
            using var offline = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            offline.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var offlineUrl = $"http://127.0.0.1:{((IPEndPoint)offline.LocalEndPoint!).Port}/whatsapp/events";
            var onlineUrl = receiver.Urls.Single() + "/whatsapp/events";
            await using var app = new TestApplication(realWebhooks: true, localWebhookUrls: [offlineUrl, onlineUrl]);
            var seed = await app.Seed();
            var tenants = app.Services.GetRequiredService<TenantService>();
            await tenants.SaveWebhook(seed.Admin, seed.TenantA, offlineUrl, true);
            await tenants.SaveWebhook(seed.Admin, seed.TenantB, onlineUrl, true);
            var transport = app.Services.GetRequiredService<IWebhookTransport>();
            var refused = await transport.Send(offlineUrl, "{}", Guid.NewGuid().ToString(), "test-only-secret", CancellationToken.None);
            Assert.False(refused.Succeeded); Assert.Null(refused.StatusCode); Assert.Contains("unavailable", refused.Error);
            var hooks = app.Services.GetRequiredService<WebhookService>();
            await hooks.Test(seed.Admin, seed.TenantA); await hooks.Test(seed.Admin, seed.TenantB);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!(await hooks.List(seed.Admin, seed.TenantA)).Any(x => x.Attempts >= 1 && x.State == "Pending") || !(await hooks.List(seed.Admin, seed.TenantB)).Any(x => x.State == "Delivered")) await Task.Delay(100, timeout.Token);
            Assert.All(received, id => Assert.Equal(seed.TenantB, id));
            Assert.False(app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
            using var client = app.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            // The worker stays alive and also handles subsequent events.
            await hooks.Test(seed.Admin, seed.TenantB);
            while ((await hooks.List(seed.Admin, seed.TenantB)).Count(x => x.State == "Delivered") < 2) await Task.Delay(100, timeout.Token);
        }
        finally { await receiver.StopAsync(); }
    }

    private static WebhookEndpointPolicy Policy(string environment, params string[] urls) => new(Options.Create(new AppOptions { DevelopmentWebhookUrls = urls }), new TestEnvironment { EnvironmentName = environment });

    [Theory]
    [InlineData("Development", "http://127.0.0.1:5190/whatsapp/events", true)]
    [InlineData("Production", "http://127.0.0.1:5190/whatsapp/events", false)]
    [InlineData("Testing", "http://127.0.0.1:5190/whatsapp/events", false)]
    [InlineData("Development", "http://127.0.0.1:5187/whatsapp/events", false)]
    [InlineData("Development", "http://127.0.0.1:5190/internal", false)]
    [InlineData("Development", "http://127.0.0.1:5190/whatsapp/events?redirect=1", false)]
    [InlineData("Development", "http://user:pass@127.0.0.1:5190/whatsapp/events", false)]
    public void OnlyExactDevelopmentReceiverIsPermitted(string environment, string url, bool allowed)
        => Assert.Equal(allowed, Policy(environment, "http://127.0.0.1:5190/whatsapp/events").AllowsLocalReceiver(new Uri(url)));

    [Fact]
    public async Task PrivateLanUrlCannotBeAllowlisted()
    {
        var url = "http://192.168.1.5:5190/whatsapp/events";
        await Assert.ThrowsAsync<AppException>(() => Policy("Development", url).Validate(url));
    }

    [Fact]
    public async Task DevelopmentTransportActuallyPostsSignedEventToLoopback()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var receiver = builder.Build();
        string? receivedBody = null, receivedSignature = null, receivedTimestamp = null;
        receiver.MapPost("/whatsapp/events", async (HttpContext context) =>
        {
            receivedBody = await new StreamReader(context.Request.Body).ReadToEndAsync();
            receivedSignature = context.Request.Headers["X-WaTenancy-Signature"];
            receivedTimestamp = context.Request.Headers["X-WaTenancy-Timestamp"];
            return Results.Ok();
        });
        await receiver.StartAsync();
        try
        {
            var url = receiver.Urls.Single() + "/whatsapp/events";
            var policy = Policy("Development", url);
            await policy.Validate(url);
            var transport = new PublicWebhookTransport(policy);
            Assert.Equal(200, (await transport.Send(url, "{\"test\":true}", Guid.NewGuid().ToString(), "test-only-secret", CancellationToken.None)).StatusCode);
            Assert.Equal("{\"test\":true}", receivedBody);
            Assert.Equal(WebhookService.Signature("test-only-secret", receivedTimestamp!, receivedBody!), receivedSignature);
            await Assert.ThrowsAsync<AppException>(() => Policy("Production", url).Validate(url));
        }
        finally { await receiver.StopAsync(); }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
