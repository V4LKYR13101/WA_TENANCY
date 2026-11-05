using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaTenancy.Web.Data;
using WaTenancy.Web.Infrastructure;
using WaTenancy.Web.Services;

namespace WaTenancy.Tests;

public sealed class IncomingMediaTests
{
    private static Task Ingest(TestApplication app,Guid account,string id="VOICE") => app.Services.GetRequiredService<IncomingEvents>().Ingest(account,
        JsonSerializer.SerializeToElement(new { ID=id,Chat="27821234567@s.whatsapp.net",Text="",Timestamp=DateTime.UtcNow,SenderJID="27821234567@s.whatsapp.net",Media=new { Type="audio",MimeType="audio/ogg; codecs=opus",FileLength=8,MediaKey="PRIVATE-KEY",DirectPath="PRIVATE-CDN-PATH" } }));
    private static IncomingMediaWorker Worker(TestApplication app) => ActivatorUtilities.CreateInstance<IncomingMediaWorker>(app.Services);
    private static async Task<CliResult> Audio(string store,IReadOnlyList<string> args,CancellationToken ct)
    {
        Assert.Contains("--read-only",args);Assert.Contains("--output",args);
        var path=args[Array.IndexOf(args.ToArray(),"--output")+1];
        await File.WriteAllBytesAsync(path,"OggSdata"u8.ToArray(),ct);
        return new(0,"{\"data\":{\"downloaded\":true,\"mime_type\":\"audio/ogg; codecs=opus\"}}","");
    }
    [Fact]
    public async Task VoiceDownloadAndWebhookAreIdempotentAndTenantProtected()
    {
        await using var app=new TestApplication();var seed=await app.Seed();await app.ConfigureHook(seed.TenantA);app.Cli.MediaHandler=Audio;
        await Ingest(app,seed.AccountA);await Ingest(app,seed.AccountA);
        await using var db=await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var file=Assert.Single(await db.Attachments.AsNoTracking().ToListAsync());Assert.Equal("Pending",file.State);
        var received=await db.WebhookDeliveries.SingleAsync(x=>x.EventType=="message.received");
        Assert.DoesNotContain("PRIVATE",received.Payload);Assert.Contains(file.Id.ToString(),received.Payload);
        using var a=app.Api(seed.KeyA);using var b=app.Api(seed.KeyB);using var anonymous=app.CreateClient();
        Assert.Equal(HttpStatusCode.Conflict,(await a.GetAsync($"/api/v1/media/{file.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await b.GetAsync($"/api/v1/media/{file.Id}/metadata")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync($"/api/v1/media/{file.Id}")).StatusCode);
        await Worker(app).Download(file.Id,CancellationToken.None);await Worker(app).Download(file.Id,CancellationToken.None);
        var response=await a.GetAsync($"/api/v1/media/{file.Id}");Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Equal("audio/ogg",response.Content.Headers.ContentType!.MediaType);Assert.Equal("OggSdata",await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound,(await b.GetAsync($"/api/v1/media/{file.Id}")).StatusCode);
        var ready=Assert.Single(await db.WebhookDeliveries.Where(x=>x.EventType=="message.media.ready").ToListAsync());
        Assert.DoesNotContain("PRIVATE",ready.Payload);Assert.DoesNotContain(app.Root,ready.Payload);
        Assert.Contains("downloadUrl",ready.Payload);Assert.Single(app.Cli.Commands,x=>x.Contains("media"));
        using var range=new HttpRequestMessage(HttpMethod.Get,$"/api/v1/media/{file.Id}");range.Headers.Range=new(0,3);
        var partial=await a.SendAsync(range);Assert.Equal(HttpStatusCode.PartialContent,partial.StatusCode);Assert.Equal("OggS",await partial.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task FailedDownloadRetriesWithoutBlockingOtherCompany()
    {
        await using var app=new TestApplication();var seed=await app.Seed();await app.ConfigureHook(seed.TenantA);await Ingest(app,seed.AccountA);await Ingest(app,seed.AccountB);
        var factory=app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();await using var db=await factory.CreateDbContextAsync();
        var a=await db.Attachments.AsNoTracking().SingleAsync(x=>x.TenantId==seed.TenantA);var b=await db.Attachments.AsNoTracking().SingleAsync(x=>x.TenantId==seed.TenantB);
        app.Cli.MediaHandler=(store,args,ct)=>store.Contains(seed.TenantA.ToString("N"))?Task.FromResult(new CliResult(1,"","Unavailable")):Audio(store,args,ct);
        await Worker(app).Download(a.Id,CancellationToken.None);await Worker(app).Download(b.Id,CancellationToken.None);
        var retry=await db.Attachments.AsNoTracking().SingleAsync(x=>x.Id==a.Id);Assert.Equal("Pending",retry.State);Assert.Equal(1,retry.Attempts);Assert.True(retry.NextAttemptAt>DateTime.UtcNow);
        Assert.Equal("Ready",(await db.Attachments.AsNoTracking().SingleAsync(x=>x.Id==b.Id)).State);
        app.Cli.MediaHandler=Audio;await Worker(app).Download(a.Id,CancellationToken.None);
        Assert.Equal("Ready",(await db.Attachments.AsNoTracking().SingleAsync(x=>x.Id==a.Id)).State);
    }
    [Fact]
    public async Task RevokedMessageCannotPublishOrDownloadAudio()
    {
        await using var app=new TestApplication();var seed=await app.Seed();await app.ConfigureHook(seed.TenantA);await Ingest(app,seed.AccountA);
        var factory=app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();await using var db=await factory.CreateDbContextAsync();var file=await db.Attachments.SingleAsync();
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Cli.MediaHandler=async(store,args,ct)=>{started.SetResult();await release.Task;return await Audio(store,args,ct);};
        var work=Worker(app).Download(file.Id,CancellationToken.None);await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await db.Attachments.Where(x=>x.Id==file.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.State,"Purged"));await db.Inbox.ExecuteDeleteAsync();release.SetResult();await work;
        Assert.False(File.Exists(app.Services.GetRequiredService<AppPaths>().AttachmentPath(seed.TenantA,file.Id)));
        Assert.False(await db.WebhookDeliveries.AnyAsync(x=>x.EventType=="message.media.ready"));
        using var client=app.Api(seed.KeyA);Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync($"/api/v1/media/{file.Id}")).StatusCode);
    }
    [Fact]
    public async Task ExceptionsStopAfterBoundedAttemptsAndPublishFailure()
    {
        await using var app=new TestApplication();var seed=await app.Seed();await app.ConfigureHook(seed.TenantA);await Ingest(app,seed.AccountA);
        app.Cli.MediaHandler=(_,_,_)=>throw new InvalidOperationException("PRIVATE-ERROR");
        await using var db=await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();var file=await db.Attachments.SingleAsync();
        for(var i=0;i<9;i++)await Worker(app).Download(file.Id,CancellationToken.None);
        var result=await db.Attachments.AsNoTracking().SingleAsync();Assert.Equal("Failed",result.State);Assert.Equal(8,result.Attempts);
        var failed=Assert.Single(await db.WebhookDeliveries.Where(x=>x.EventType=="message.media.failed").ToListAsync());Assert.DoesNotContain("PRIVATE-ERROR",failed.Payload);
    }
    [Fact]
    public async Task CompanyQuotaDoesNotConsumeAnotherCompanysAllowance()
    {
        await using var app=new TestApplication();var seed=await app.Seed();app.Cli.MediaHandler=Audio;
        var settings=app.Services.GetRequiredService<IOptions<AppOptions>>().Value;settings.MaxIncomingMediaBytes=8;settings.MaxTenantIncomingMediaBytes=8;
        await Ingest(app,seed.AccountA,"ONE");await Ingest(app,seed.AccountA,"TWO");await Ingest(app,seed.AccountB,"THREE");
        await using var db=await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        foreach(var file in await db.Attachments.AsNoTracking().OrderBy(x=>x.CreatedAt).ToListAsync())await Worker(app).Download(file.Id,CancellationToken.None);
        var files=await db.Attachments.AsNoTracking().ToListAsync();Assert.Equal("Ready",files.Single(x=>x.MessageId=="ONE").State);Assert.Equal("Failed",files.Single(x=>x.MessageId=="TWO").State);Assert.Equal("Ready",files.Single(x=>x.MessageId=="THREE").State);
    }
    [Fact]
    public async Task SlowCompanyDoesNotBlockOtherCompanysBackgroundDownload()
    {
        await using var app=new TestApplication(workers:true);var seed=await app.Seed();
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Cli.MediaHandler=async(store,args,ct)=>{if(store.Contains(seed.TenantA.ToString("N"))){started.TrySetResult();await release.Task.WaitAsync(ct);}return await Audio(store,args,ct);};
        try
        {
            await Ingest(app,seed.AccountA);await started.Task.WaitAsync(TimeSpan.FromSeconds(10));await Ingest(app,seed.AccountB);
            var factory=app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while(true){await using var db=await factory.CreateDbContextAsync();if(await db.Attachments.AnyAsync(x=>x.TenantId==seed.TenantB&&x.State=="Ready"))break;await Task.Delay(100,timeout.Token);}
            Assert.False(release.Task.IsCompleted);
        }
        finally{release.TrySetResult();}
    }
    [Fact]
    public async Task FailedMediaCanBeRequeuedOnlyByItsCompany()
    {
        await using var app=new TestApplication();var seed=await app.Seed();await Ingest(app,seed.AccountA);
        await using var db=await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();var file=await db.Attachments.SingleAsync();await db.Attachments.ExecuteUpdateAsync(s=>s.SetProperty(x=>x.State,"Failed").SetProperty(x=>x.Attempts,8));
        using var a=app.Api(seed.KeyA);using var b=app.Api(seed.KeyB);Assert.Equal(HttpStatusCode.NotFound,(await b.PostAsync($"/api/v1/media/{file.Id}/retry",null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,(await a.PostAsync($"/api/v1/media/{file.Id}/retry",null)).StatusCode);Assert.Equal(HttpStatusCode.Conflict,(await a.PostAsync($"/api/v1/media/{file.Id}/retry",null)).StatusCode);
        var refreshed=await db.Attachments.AsNoTracking().SingleAsync();Assert.Equal("Pending",refreshed.State);Assert.Equal(0,refreshed.Attempts);
    }
}
