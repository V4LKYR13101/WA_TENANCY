using System.Net;
using Microsoft.Extensions.Options;
using WaTenancy.Web.Infrastructure;

namespace WaTenancy.Web.Services;

public sealed class WebhookEndpointPolicy(IOptions<AppOptions> options, IHostEnvironment environment)
{
    public IEnumerable<string> LocalReceiverUrls => options.Value.DevelopmentWebhookUrls.Where(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && AllowsLocalReceiver(uri));
    public bool AllowsLocalReceiver(Uri uri) => environment.IsDevelopment()
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && uri.Scheme is "http" or "https"
        && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.DnsSafeHost, out var address) && IPAddress.IsLoopback(address))
        && options.Value.DevelopmentWebhookUrls.Any(value => Uri.TryCreate(value, UriKind.Absolute, out var allowed) && allowed == uri);

    public Task Validate(string url)
    {
        if (url.Length <= 2048 && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && AllowsLocalReceiver(uri)) return Task.CompletedTask;
        return PublicWebhookTransport.ValidateUrl(url);
    }
}
