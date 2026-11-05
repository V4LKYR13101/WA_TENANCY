using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WaTenancy.Web.Data;
using WaTenancy.Web.Security;
using WaTenancy.Web.Services;
namespace WaTenancy.Tests;
public sealed class TenantApiTests
{
    [Fact] public async Task AnonymousApiRequestsCannotReadAccounts()
    {
        await using var app = new TestApplication(); using var client = app.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/accounts")).StatusCode);
    }
    [Fact] public async Task ApiKeysSeeOnlyTheirOwnCompanyAndDoNotLeakSessionSecrets()
    {
        await using var app = new TestApplication(); var seed = await app.Seed(); using var client = app.Api(seed.KeyA);
        var content = await client.GetStringAsync("/api/v1/accounts"); Assert.Contains("Alpha Sales", content); Assert.DoesNotContain("Beta Sales", content);
        using var json = JsonDocument.Parse(content); Assert.False(json.RootElement[0].TryGetProperty("protectedWebhookSecret", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/accounts/{seed.AccountB}/pairing")).StatusCode);
    }
    [Fact] public async Task ReadOnlyKeyCannotSendOrManageAccounts()
    {
        await using var app = new TestApplication(); var seed = await app.Seed(); var key = await app.Services.GetRequiredService<TenantService>().CreateKey(seed.Admin, seed.TenantA, "Read only", false, false, 30); using var client = app.Api(key);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/accounts", new { name = "Other" })).StatusCode);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "readonly-test"); Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/messages", new SendRequest(seed.AccountA, "+27821234567", "hello"))).StatusCode);
    }
    [Fact] public async Task RevokedKeysAndDisabledTenantsAreRejected()
    {
        await using var app = new TestApplication(); var seed = await app.Seed(); using var client = app.Api(seed.KeyA); var service = app.Services.GetRequiredService<TenantService>();
        var key = (await service.Keys(seed.Admin, seed.TenantA)).Single(); await service.RevokeKey(seed.Admin, seed.TenantA, key.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
        await service.SetCompanyEnabled(seed.Admin, seed.TenantB, false); using var b = app.Api(seed.KeyB); Assert.Equal(HttpStatusCode.Unauthorized, (await b.GetAsync("/api/v1/me")).StatusCode);
    }
    [Fact] public async Task IdempotentSendsReturnSameJobAndRejectDifferentPayloads()
    {
        await using var app = new TestApplication(); var seed = await app.Seed(); await app.Connect(seed.AccountA); using var client = app.Api(seed.KeyA); client.DefaultRequestHeaders.Add("Idempotency-Key", "order-confirmation-100");
        var request = new SendRequest(seed.AccountA, "+27821234567", "hello");
        var first = await client.PostAsJsonAsync("/api/v1/messages", request); Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var second = await client.PostAsJsonAsync("/api/v1/messages", request); Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync()); var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync()); Assert.Equal(firstJson.RootElement.GetProperty("id").GetString(), secondJson.RootElement.GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/messages", request with { Text = "different" })).StatusCode);
        Assert.Empty(app.Cli.Commands); // A queued API request never invokes a send directly.
    }
    [Fact] public async Task CompanyCannotSendFromAnotherCompaniesNumberOrAttachment()
    {
        await using var app = new TestApplication(); var seed = await app.Seed(); await app.Connect(seed.AccountA); using var client = app.Api(seed.KeyA); client.DefaultRequestHeaders.Add("Idempotency-Key", "cross-company-test");
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/v1/messages", new SendRequest(seed.AccountB, "+27821234567", "wrong company"))).StatusCode);
        var messages = app.Services.GetRequiredService<MessageService>(); var file = await messages.Upload(seed.Admin, seed.TenantB, "document.txt", "text/plain", new MemoryStream([1, 2, 3]), 3);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/v1/messages", new SendRequest(seed.AccountA, "+27821234567", "file", file.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/files/{file.Id}")).StatusCode);
    }
    [Fact] public async Task ViewerCannotManageMembersOrReadAnotherTenant()
    {
        await using var app = new TestApplication(); var seed = await app.Seed(); var service = app.Services.GetRequiredService<TenantService>();
        Assert.Equal(403, (await Assert.ThrowsAsync<AppException>(() => service.CreateAccount(seed.Viewer, seed.TenantA, "Blocked"))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<AppException>(() => service.Accounts(seed.Viewer, seed.TenantB))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<AppException>(() => service.RotateWebhookSecret(seed.Viewer, seed.TenantA))).Status);
    }
    [Fact] public async Task IncomingMessageIsTenantScopedDeduplicatedAndQueuedForWebhook()
    {
        await using var app = new TestApplication(); var seed = await app.Seed(); await app.ConfigureHook(seed.TenantA);
        var evt = JsonSerializer.SerializeToElement(new { Chat = "27821234567@s.whatsapp.net", ID = "INBOUND-1", Timestamp = DateTime.UtcNow, Text = "customer message", FromMe = false, SenderJID = "27821234567@s.whatsapp.net", ChatName = "Customer" });
        var incoming = app.Services.GetRequiredService<IncomingEvents>(); await incoming.Ingest(seed.AccountA, evt); await incoming.Ingest(seed.AccountA, evt);
        var messages = app.Services.GetRequiredService<MessageService>(); Assert.Single(await messages.Inbox(seed.Admin, seed.TenantA)); Assert.Empty(await messages.Inbox(seed.Admin, seed.TenantB));
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(); var delivery = await db.WebhookDeliveries.SingleAsync(x => x.EventType == "message.received");
        using var json = JsonDocument.Parse(delivery.Payload); Assert.Equal(seed.TenantA.ToString(), json.RootElement.GetProperty("tenantId").GetString()); Assert.Equal(seed.AccountA.ToString(), json.RootElement.GetProperty("accountId").GetString()); Assert.Equal("customer message", json.RootElement.GetProperty("data").GetProperty("text").GetString());
    }
    [Fact] public async Task InternalWebhookRejectsBadSignaturesAndAcceptsAuthenticatedPayload()
    {
        await using var app = new TestApplication(); var seed = await app.Seed(); using var client = app.CreateClient(); var body = "{\"ID\":\"SIGNED-ID\",\"Chat\":\"27821234567@s.whatsapp.net\",\"Text\":\"signed\"}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/internal/wacli/{seed.AccountA}", new StringContent(body, Encoding.UTF8, "application/json"))).StatusCode);
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(); var account = await db.Accounts.FindAsync(seed.AccountA);
        var secret = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("WaTenancy.Secrets.v1").Unprotect(account!.ProtectedWebhookSecret);
        client.DefaultRequestHeaders.Add("X-Wacli-Signature", "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/internal/wacli/{seed.AccountA}", new StringContent(body, Encoding.UTF8, "application/json"))).StatusCode);
    }
    [Fact] public async Task RevokedBrowserMembershipIsCheckedOnEveryOperation()
    {
        await using var app = new TestApplication(); var seed = await app.Seed(); await using var db = await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        await db.Memberships.Where(x => x.UserId == TenantAccess.Actor(seed.Viewer)).ExecuteDeleteAsync(); Assert.Equal(404, (await Assert.ThrowsAsync<AppException>(() => app.Services.GetRequiredService<TenantService>().Accounts(seed.Viewer, seed.TenantA))).Status);
    }
    [Fact] public async Task LastOwnerCannotBeRemoved()
    {
        await using var app = new TestApplication(); var seed = await app.Seed(); var service = app.Services.GetRequiredService<TenantService>();
        await service.AddMember(seed.Admin, seed.TenantA, "owner@example.test", "TestPassword!2026", TenantRole.Owner); var member = (await service.Members(seed.Admin, seed.TenantA)).Single(x => x.Role == TenantRole.Owner);
        Assert.Equal(409, (await Assert.ThrowsAsync<AppException>(() => service.RemoveMember(seed.Admin, seed.TenantA, member.UserId))).Status);
    }
}
