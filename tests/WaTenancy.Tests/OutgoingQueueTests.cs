using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WaTenancy.Web.Data;
using WaTenancy.Web.Infrastructure;
using WaTenancy.Web.Security;
using WaTenancy.Web.Services;

namespace WaTenancy.Tests;

public sealed class OutgoingQueueTests
{
    private static async Task Until(Func<Task<bool>> condition)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while(!await condition())await Task.Delay(100,timeout.Token);
    }
    private static async Task Due(TestApplication app,Guid id)
    {
        await using var db=await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        await db.Jobs.Where(x=>x.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.NextAttemptAt,DateTime.UtcNow));
    }
    [Fact]
    public async Task DisconnectedNumberAcceptsDurableJobAndSendsWhenReconnected()
    {
        await using var app=new TestApplication(workers:true);var seed=await app.Seed();var service=app.Services.GetRequiredService<MessageService>();
        using var api=app.Api(seed.KeyA);api.DefaultRequestHeaders.Add("Idempotency-Key","offline-durable-message");
        var response=await api.PostAsJsonAsync("/api/v1/messages",new SendRequest(seed.AccountA,"+27821234567","offline queue"));
        Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        var job=(await service.Jobs(seed.Admin,seed.TenantA)).Single();Assert.Equal(JobState.Queued,job.State);Assert.Equal(0,job.Attempts);
        await Task.Delay(1200);Assert.DoesNotContain(app.Cli.Commands,args=>args.Contains("send"));
        await app.Connect(seed.AccountA);
        await Until(async()=> (await service.Job(seed.Admin,seed.TenantA,job.Id)).State==JobState.Sent);
        Assert.Single(app.Cli.Commands,args=>args.Contains("send"));
    }
    [Theory]
    [InlineData("store is locked","")]
    [InlineData("it was not sent","")]
    [InlineData("","{\"success\":false,\"error\":\"not authenticated\"}")]
    public async Task DefinitelyRejectedSendsRetryWithBackoffAndKeepSameJob(string error,string output)
    {
        await using var app=new TestApplication(workers:true);var seed=await app.Seed();await app.Connect(seed.AccountA);await app.ConfigureHook(seed.TenantA);
        var service=app.Services.GetRequiredService<MessageService>();app.Cli.SendResult=new CliResult(1,output,error);
        var job=await service.Enqueue(seed.Admin,seed.TenantA,new(seed.AccountA,"+27821234567","safe retry"),"known-rejected-retry");
        await Until(async()=> (await service.Job(seed.Admin,seed.TenantA,job.Id)) is { State:JobState.Queued,Attempts:1 });
        var waiting=await service.Job(seed.Admin,seed.TenantA,job.Id);Assert.True(waiting.NextAttemptAt>DateTime.UtcNow);Assert.Null(waiting.CompletedAt);
        app.Cli.SendResult=new CliResult(0,"{\"success\":true,\"data\":{\"sent\":true,\"id\":\"RETRY-SENT\"}}","");await Due(app,job.Id);
        await Until(async()=> (await service.Job(seed.Admin,seed.TenantA,job.Id)).State==JobState.Sent);
        Assert.Equal(2,(await service.Job(seed.Admin,seed.TenantA,job.Id)).Attempts);
        Assert.Single(await service.Jobs(seed.Admin,seed.TenantA));
        Assert.All(app.Cli.Commands.Where(args=>args.Contains("send")),args=>Assert.Contains("--lock-wait",args));
        await using var db=await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        Assert.Equal(2,await db.WebhookDeliveries.CountAsync(x=>x.TenantId==seed.TenantA&&x.EventType=="message.dispatch"));
    }
    [Fact]
    public async Task RetryLimitAndManualRetryAreScopedToTenantAndRejectUnknown()
    {
        await using var app=new TestApplication(workers:true);var seed=await app.Seed();await app.Connect(seed.AccountA);
        app.Services.GetRequiredService<IOptions<AppOptions>>().Value.MaxSendAttempts=2;app.Cli.SendResult=new CliResult(1,"","store is locked");
        var service=app.Services.GetRequiredService<MessageService>();var job=await service.Enqueue(seed.Admin,seed.TenantA,new(seed.AccountA,"+27821234567","bounded retry"),"retry-exhaustion-message");
        await Until(async()=> (await service.Job(seed.Admin,seed.TenantA,job.Id)).Attempts==1);await Due(app,job.Id);
        await Until(async()=> (await service.Job(seed.Admin,seed.TenantA,job.Id)).State==JobState.Failed);
        Assert.Equal(2,(await service.Job(seed.Admin,seed.TenantA,job.Id)).Attempts);
        Assert.Equal(404,(await Assert.ThrowsAsync<AppException>(()=>service.Retry(seed.Admin,seed.TenantB,job.Id))).Status);
        app.Cli.SendResult=new CliResult(0,"{\"data\":{\"sent\":true,\"id\":\"MANUAL-RETRY\"}}","");
        await service.Retry(seed.Admin,seed.TenantA,job.Id);
        await Until(async()=> (await service.Job(seed.Admin,seed.TenantA,job.Id)).State==JobState.Sent);
        await using var db=await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        await db.Jobs.Where(x=>x.Id==job.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.State,JobState.Unknown));
        Assert.Equal(409,(await Assert.ThrowsAsync<AppException>(()=>service.Retry(seed.Admin,seed.TenantA,job.Id))).Status);
    }
    [Fact]
    public async Task RetryPreservesOrderWithinAccount()
    {
        await using var app=new TestApplication(workers:true);var seed=await app.Seed();await app.Connect(seed.AccountA);app.Cli.SendResult=new CliResult(1,"","it was not sent");
        var service=app.Services.GetRequiredService<MessageService>();var first=await service.Enqueue(seed.Admin,seed.TenantA,new(seed.AccountA,"+27821234567","first"),"ordered-message-first");
        await Until(async()=> (await service.Job(seed.Admin,seed.TenantA,first.Id)).Attempts==1);
        var second=await service.Enqueue(seed.Admin,seed.TenantA,new(seed.AccountA,"+27821234567","second"),"ordered-message-second");
        app.Cli.SendResult=new CliResult(0,"{\"data\":{\"sent\":true,\"id\":\"ORDERED\"}}","");await Task.Delay(700);
        Assert.Single(app.Cli.Commands,args=>args.Contains("send"));await Due(app,first.Id);
        await Until(async()=> (await service.Job(seed.Admin,seed.TenantA,second.Id)).State==JobState.Sent);
        Assert.Equal(new[]{"first","first","second"},app.Cli.Commands.Where(args=>args.Contains("send")).Select(args=>args[Array.IndexOf(args,"--message")+1]).ToArray());
    }
    [Fact]
    public async Task SlowNumberDoesNotBlockAnotherTenantsNumber()
    {
        await using var app=new TestApplication(workers:true);var seed=await app.Seed();await app.Connect(seed.AccountA);await app.Connect(seed.AccountB);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource<CliResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var paths=app.Services.GetRequiredService<AppPaths>();app.Cli.SendHandler=(store,args,ct)=>
        {
            if(store!=paths.Store(seed.TenantA,seed.AccountA))return Task.FromResult(new CliResult(0,"{\"data\":{\"sent\":true,\"id\":\"OTHER-NUMBER\"}}",""));
            started.TrySetResult();return release.Task.WaitAsync(ct);
        };
        var service=app.Services.GetRequiredService<MessageService>();
        try
        {
            await service.Enqueue(seed.Admin,seed.TenantA,new(seed.AccountA,"+27821234567","slow"),"slow-number-test-message");await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var other=await service.Enqueue(seed.Admin,seed.TenantB,new(seed.AccountB,"+27821234567","independent"),"other-number-test-message");
            await Until(async()=> (await service.Job(seed.Admin,seed.TenantB,other.Id)).State==JobState.Sent);Assert.False(release.Task.IsCompleted);
        }
        finally{release.TrySetResult(new CliResult(0,"{\"data\":{\"sent\":true,\"id\":\"RELEASED\"}}",""));}
    }
    [Fact]
    public async Task MalformedApiRequestReturns400InsteadOf500()
    {
        await using var app=new TestApplication();var seed=await app.Seed();using var api=app.Api(seed.KeyA);api.DefaultRequestHeaders.Add("Idempotency-Key","bad-http-request-test");
        using var body=new StringContent("{\"accountId\":\"not-a-guid\",\"recipient\":\"+27821234567\",\"text\":\"hello\"}",System.Text.Encoding.UTF8,"application/json");
        Assert.Equal(HttpStatusCode.BadRequest,(await api.PostAsync("/api/v1/messages",body)).StatusCode);
    }
}
