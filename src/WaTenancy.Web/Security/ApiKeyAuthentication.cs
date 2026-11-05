using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WaTenancy.Web.Data;

namespace WaTenancy.Web.Security;

public sealed class ApiKeyAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IDbContextFactory<AppDbContext> factory)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TenantApiKey";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var values)) return AuthenticateResult.NoResult();
        var token = values.ToString();
        if (token.Length is < 64 or > 128) return AuthenticateResult.Fail("Invalid API key.");
        await using var db = await factory.CreateDbContextAsync(Context.RequestAborted);
        var hash = SecretTokens.Hash(token);
        var key = await db.ApiKeys.Include(x => x.Tenant).SingleOrDefaultAsync(x => x.Hash == hash, Context.RequestAborted);
        if (key is null || !key.Tenant.IsEnabled || key.RevokedAt != null || key.ExpiresAt <= DateTime.UtcNow)
            return AuthenticateResult.Fail("Invalid, expired or revoked API key.");
        Claim[] claims = [new(ClaimTypes.NameIdentifier, "api:" + key.Id), new("auth_method", "api_key"), new("tenant_id", key.TenantId.ToString()), new("api_key_id", key.Id.ToString())];
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }
}
