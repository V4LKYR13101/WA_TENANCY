using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WaTenancy.Web.Data;
using WaTenancy.Web.Infrastructure;
using WaTenancy.Web.Services;

namespace WaTenancy.Tests;

public sealed class WorkerTests
{
    [Fact]
    public async Task PendingUnauthorizedDeliveryCanBeRetriedWithCurrentSecret()
    {
        await using var app=new TestApplication(); var seed=await app.Seed(); await app.ConfigureHook(seed.TenantA); app.Transport.Status=401;
        var hooks=app.Services.GetRequiredService<WebhookService>(); await hooks.Test(seed.Admin,seed.TenantA);
        await WaitFor(async()=> (await hooks.List(seed.Admin,seed.TenantA)).Any(x=>x.State=="Pending" && x.LastStatusCode==401));
        var delivery=(await hooks.List(seed.Admin,seed.TenantA)).Single(); var oldSecret=app.Transport.Requests.Single().Secret;
        var currentSecret=await app.Services.GetRequiredService<TenantService>().RotateWebhookSecret(seed.Admin,seed.TenantA);
        app.Transport.Status=200;
        await hooks.Retry(seed.Admin,seed.TenantA,delivery.Id);
        await WaitFor(async()=> (await hooks.List(seed.Admin,seed.TenantA)).Any(x=>x.State=="Delivered"));
        Assert.NotEqual(oldSecret,currentSecret); Assert.Equal(currentSecret,app.Transport.Requests.Last().Secret);
        Assert.Equal(delivery.Id.ToString(),app.Transport.Requests.Last().Id);
    }
    [Theory]
    [InlineData("connection")]
    [InlineData("timeout")]
    [InlineData("unexpected")]
    public async Task OneTenantsDeliveryFailureDoesNotStopHostOrOtherTenant(string failure)
    {
        await using var app = new TestApplication(); var seed = await app.Seed();
        await app.ConfigureHook(seed.TenantA); await app.ConfigureHook(seed.TenantB);
        app.Transport.Handler = (payload, ct) =>
        {
            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.GetProperty("tenantId").GetGuid() != seed.TenantA) return Task.FromResult(200);
            return Task.FromException<int>(failure switch
            {
                "connection" => new HttpRequestException("Test connection refused"),
                "timeout" => new TaskCanceledException("Test request timeout"),
                _ => new InvalidOperationException("Test unexpected delivery failure")
            });
        };
        var hooks = app.Services.GetRequiredService<WebhookService>();
        await hooks.Test(seed.Admin, seed.TenantA); await hooks.Test(seed.Admin, seed.TenantB);
        await WaitFor(async () => (await hooks.List(seed.Admin, seed.TenantA)).Any(x => x.Attempts == 1 && x.State == "Pending") && (await hooks.List(seed.Admin, seed.TenantB)).Any(x => x.State == "Delivered"));
        using var client = app.CreateClient();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.NotNull((await hooks.List(seed.Admin, seed.TenantA)).Single().LastError);
    }

    [Fact]
    public async Task SlowTenantDoesNotBlockNewDeliveriesForAnotherTenant()
    {
        await using var app = new TestApplication(); var seed = await app.Seed();
        await app.ConfigureHook(seed.TenantA); await app.ConfigureHook(seed.TenantB);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Transport.Handler = (payload, ct) =>
        {
            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.GetProperty("tenantId").GetGuid() != seed.TenantA) return Task.FromResult(200);
            started.TrySetResult(); return release.Task.WaitAsync(ct);
        };
        var hooks = app.Services.GetRequiredService<WebhookService>();
        try
        {
            await hooks.Test(seed.Admin, seed.TenantA);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await hooks.Test(seed.Admin, seed.TenantA);
            await hooks.Test(seed.Admin, seed.TenantB);
            await WaitFor(async () => (await hooks.List(seed.Admin, seed.TenantB)).Any(x => x.State == "Delivered"));
            Assert.False(release.Task.IsCompleted);
            Assert.Single(app.Transport.Requests, r => JsonDocument.Parse(r.Payload).RootElement.GetProperty("tenantId").GetGuid() == seed.TenantA);
            release.TrySetResult(200);
            await WaitFor(async () => (await hooks.List(seed.Admin, seed.TenantA)).All(x => x.State == "Delivered"));
        }
        finally { release.TrySetResult(200); }
    }

    private static async Task WaitFor(Func<Task<bool>> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await check()) await Task.Delay(100, timeout.Token);
    }
    private static async Task<WhatsAppAccount> Account(TestApplication app, Guid id)
    {
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == id);
    }
    [Fact] public async Task AllNumbersHaveIndependentWatchersAndLogoutDoesNotRestart()
    {
        await using var app = new TestApplication(workers: true); var seed = await app.Seed(); var supervisor = app.Services.GetRequiredService<AccountSupervisor>(); var paths = app.Services.GetRequiredService<AppPaths>();
        await supervisor.StartPairing(seed.Admin, seed.TenantA, seed.AccountA);
        await supervisor.StartPairing(seed.Admin, seed.TenantB, seed.AccountB);
        await WaitFor(async () => (await supervisor.Pairing(seed.Admin, seed.TenantA, seed.AccountA)).QrImage is not null);
        Assert.StartsWith("data:image/png;base64,", (await supervisor.Pairing(seed.Admin, seed.TenantA, seed.AccountA)).QrImage);
        var aStore = paths.Store(seed.TenantA, seed.AccountA); var bStore = paths.Store(seed.TenantB, seed.AccountB);
        app.Cli.Sessions[aStore].Exit(0); app.Cli.Sessions[bStore].Exit(0);
        await WaitFor(async () => (await Account(app, seed.AccountA)).State == AccountState.Connected && (await Account(app, seed.AccountB)).State == AccountState.Connected);
        Assert.NotSame(app.Cli.Sessions[aStore], app.Cli.Sessions[bStore]);
        await supervisor.SetWatch(seed.Admin, seed.TenantA, seed.AccountA, false);
        Assert.Equal(AccountState.Paused, (await Account(app, seed.AccountA)).State); Assert.Equal(AccountState.Connected, (await Account(app, seed.AccountB)).State);
        await app.Cli.Sessions[bStore].Emit("logged_out", new { }); app.Cli.Sessions[bStore].Exit(0);
        await WaitFor(async () => (await Account(app, seed.AccountB)).State == AccountState.NeedsPairing);
        Assert.False((await Account(app, seed.AccountB)).WatchEnabled);
        Assert.Contains(app.Cli.Commands, args => args.Contains("--webhook-events") && args.Contains("message,receipt,chat_presence"));
    }
    [Fact] public async Task SyncFailureRestartsWorkerWithBackoff()
    {
        await using var app = new TestApplication(workers: true); var seed = await app.Seed(); var paths = app.Services.GetRequiredService<AppPaths>(); await app.Connect(seed.AccountA);
        var store = paths.Store(seed.TenantA, seed.AccountA);
        await WaitFor(() => Task.FromResult(app.Cli.Sessions.ContainsKey(store)));
        var original = app.Cli.Sessions[store]; original.Exit(1);
        await WaitFor(() => Task.FromResult(app.Cli.Sessions.TryGetValue(store, out var next) && !ReferenceEquals(original, next)));
        Assert.False(app.Cli.Sessions[store].Completion.IsCompleted);
    }
    [Fact] public async Task UncertainSendIsNotAutomaticallyRepeated()
    {
        await using var app = new TestApplication(workers: true); var seed = await app.Seed(); app.Cli.SendResult = new CliResult(1, "", "timeout", true); await app.Connect(seed.AccountA);
        var messages = app.Services.GetRequiredService<MessageService>(); var job = await messages.Enqueue(seed.Admin, seed.TenantA, new(seed.AccountA, "+27821234567", "single dispatch"), "timeout-idempotency-key");
        await WaitFor(async () => (await messages.Job(seed.Admin, seed.TenantA, job.Id)).State == JobState.Unknown);
        await Task.Delay(1500);
        Assert.Single(app.Cli.Commands, args => args.Contains("send"));
    }
    [Fact] public async Task ReceiptsAdvanceDeliveredAndReadWithoutDowngrading()
    {
        await using var app = new TestApplication(); var seed = await app.Seed(); await app.Connect(seed.AccountA); var messages = app.Services.GetRequiredService<MessageService>(); var incoming = app.Services.GetRequiredService<IncomingEvents>();
        var job = await messages.Enqueue(seed.Admin, seed.TenantA, new(seed.AccountA, "+27821234567", "receipt"), "receipt-idempotency-key");
        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync()) await db.Jobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, JobState.Sent).SetProperty(x => x.WhatsAppMessageId, "RECEIPT-ID"));
        await incoming.Ingest(seed.AccountA, JsonSerializer.SerializeToElement(new { EventType = "receipt", Type = "read", MessageIDs = new[] { "RECEIPT-ID" } }));
        await incoming.Ingest(seed.AccountA, JsonSerializer.SerializeToElement(new { EventType = "receipt", Type = "delivered", MessageIDs = new[] { "RECEIPT-ID" } }));
        Assert.Equal(JobState.Read, (await messages.Job(seed.Admin, seed.TenantA, job.Id)).State);
    }
    [Fact] public async Task WebhooksRetryWithStablePayloadAndCapturedSecret()
    {
        await using var app = new TestApplication(); var seed = await app.Seed(); await app.ConfigureHook(seed.TenantA); app.Transport.Status = 500;
        var hooks = app.Services.GetRequiredService<WebhookService>(); await hooks.Test(seed.Admin, seed.TenantA);
        await WaitFor(async () => (await hooks.List(seed.Admin, seed.TenantA)).Any(x => x.Attempts == 1 && x.State == "Pending"));
        var first = app.Transport.Requests.Single(); await app.Services.GetRequiredService<TenantService>().RotateWebhookSecret(seed.Admin, seed.TenantA);
        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync()) await db.WebhookDeliveries.ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAt, DateTime.UtcNow));
        app.Transport.Status = 200;
        await WaitFor(async () => (await hooks.List(seed.Admin, seed.TenantA)).Any(x => x.State == "Delivered"));
        var second = app.Transport.Requests.Last(); Assert.Equal(first.Id, second.Id); Assert.Equal(first.Payload, second.Payload); Assert.Equal(first.Secret, second.Secret);
    }
    [Fact] public async Task DisabledWebhookQueuesPauseWithoutExternalDelivery()
    {
        await using var app = new TestApplication(); var seed = await app.Seed(); await app.ConfigureHook(seed.TenantA);
        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            var hook = app.Services.GetRequiredService<WebhookService>(); await hook.Queue(db, seed.TenantA, seed.AccountA, "webhook.test", new { }, "disabled-test");
            var tenant = await db.Tenants.FindAsync(seed.TenantA); tenant!.WebhookEnabled = false; await db.SaveChangesAsync();
        }
        await Task.Delay(2200); Assert.Empty(app.Transport.Requests);
    }
}
