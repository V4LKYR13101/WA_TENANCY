using System.Net;
using WaTenancy.Web.Infrastructure;
using WaTenancy.Web.Security;
using WaTenancy.Web.Services;
namespace WaTenancy.Tests;
public sealed class SecurityTests
{
    [Theory]
    [InlineData("127.0.0.1")][InlineData("10.1.2.3")][InlineData("172.16.0.1")][InlineData("192.168.1.10")][InlineData("169.254.169.254")][InlineData("100.64.1.1")][InlineData("::1")][InlineData("::ffff:127.0.0.1")][InlineData("fe80::1")][InlineData("fc00::1")][InlineData("2001:db8::1")][InlineData("0.0.0.0")][InlineData("224.1.1.1")]
    public void WebhooksRejectPrivateAndReservedAddresses(string ip) => Assert.False(PublicWebhookTransport.IsPublic(IPAddress.Parse(ip)));
    [Theory][InlineData("1.1.1.1")][InlineData("8.8.8.8")][InlineData("2606:4700:4700::1111")]
    public void WebhooksPermitPublicAddresses(string ip) => Assert.True(PublicWebhookTransport.IsPublic(IPAddress.Parse(ip)));
    [Theory][InlineData("http://example.com/hook")][InlineData("https://user:pass@example.com/hook")][InlineData("https://127.0.0.1/hook")][InlineData("https://example.com:8443/hook")]
    public async Task InvalidWebhookUrlsAreRejected(string url) => await Assert.ThrowsAsync<AppException>(() => PublicWebhookTransport.ValidateUrl(url));
    [Theory][InlineData("& calc.exe")][InlineData("-flag")][InlineData("name")][InlineData("../../session")][InlineData("")]
    public void InvalidRecipientsAreRejected(string value) => Assert.Throws<AppException>(() => MessageService.Recipient(value));
    [Theory][InlineData("+27821234567")][InlineData("27821234567@s.whatsapp.net")][InlineData("120363000000000000@g.us")]
    public void ExplicitRecipientsAreAccepted(string value) => Assert.Equal(value, MessageService.Recipient(value));
    [Fact] public void ProcessArgumentsDoNotGoThroughShell()
    {
        var value = "literal & $(calc.exe) \"quoted\"";
        var info = WacliClient.BuildStartInfo("wacli", @"C:\data\tenant\account", ["send", "text", "--message", value]);
        Assert.False(info.UseShellExecute); Assert.True(info.CreateNoWindow); Assert.Contains(value, info.ArgumentList); Assert.Empty(info.Arguments);
    }
    [Fact] public void SignatureChangesIfBodyOrTimestampChanges()
    {
        var signature = WebhookService.Signature("secret", "123", "{}");
        Assert.StartsWith("sha256=", signature); Assert.Equal(71, signature.Length);
        Assert.NotEqual(signature, WebhookService.Signature("secret", "124", "{}")); Assert.NotEqual(signature, WebhookService.Signature("secret", "123", "{ }"));
    }
    [Fact] public void StructuredCliEventsAndResultsAreParsed()
    {
        var e = CliJson.Lifecycle("{\"event\":\"qr_code\",\"data\":{\"code\":\"secret\"},\"ts\":123}");
        Assert.Equal("qr_code", e!.Event); Assert.Equal("secret", CliJson.String(e.Data, "code")); Assert.Null(CliJson.Lifecycle("human text"));
        Assert.True(CliJson.Bool(CliJson.Parse("{\"success\":true,\"data\":{\"sent\":true}}"), "sent"));
    }
}
