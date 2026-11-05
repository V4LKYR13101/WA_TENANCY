using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WaTenancy.Web.Data;

namespace WaTenancy.Web.Security;

public sealed class AppException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
public static class SecretTokens
{
    public static string Generate() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
public sealed class TenantAccess(IDbContextFactory<AppDbContext> factory)
{
    public static string Actor(ClaimsPrincipal principal) => principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
    public async Task<bool> IsPlatformAdmin(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim("auth_method", "api_key")) return false;
        await using var db = await factory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(Actor(principal));
        return user is { IsEnabled: true, IsPlatformAdmin: true } && user.SecurityStamp == principal.FindFirstValue("security_stamp");
    }
    public async Task RequirePlatformAdmin(ClaimsPrincipal principal)
    {
        if (!await IsPlatformAdmin(principal)) throw new AppException(403, "Platform administrator access is required.");
    }
    public async Task<TenantRole> Require(ClaimsPrincipal principal, Guid tenantId, TenantRole minimum = TenantRole.Viewer, string? scope = null)
    {
        if (principal.Identity?.IsAuthenticated != true) throw new AppException(401, "Please sign in.");
        await using var db = await factory.CreateDbContextAsync();
        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant is null || !tenant.IsEnabled) throw new AppException(404, "Company not found or disabled.");
        if (principal.HasClaim("auth_method", "api_key"))
        {
            if (principal.FindFirstValue("tenant_id") != tenantId.ToString()) throw new AppException(404, "Resource not found.");
            var id = Guid.Parse(principal.FindFirstValue("api_key_id")!);
            var key = await db.ApiKeys.FindAsync(id);
            if (key is null || key.RevokedAt != null || key.ExpiresAt <= DateTime.UtcNow) throw new AppException(401, "API key has expired or was revoked.");
            if (scope == "send" && !key.CanSend || scope == "accounts" && !key.CanManageAccounts || minimum >= TenantRole.Admin && scope is null)
                throw new AppException(403, "API key does not have this permission.");
            return scope == "accounts" ? TenantRole.Admin : key.CanSend ? TenantRole.Agent : TenantRole.Viewer;
        }
        var user = await db.Users.FindAsync(Actor(principal));
        if (user is null || !user.IsEnabled || user.SecurityStamp != principal.FindFirstValue("security_stamp")) throw new AppException(401, "Session expired. Please sign in again.");
        if (user.IsPlatformAdmin) return TenantRole.Owner;
        var membership = await db.Memberships.FindAsync(tenantId, user.Id);
        if (membership is null) throw new AppException(404, "Resource not found.");
        if (membership.Role < minimum) throw new AppException(403, "Your company role does not allow this action.");
        return membership.Role;
    }
    public async Task<List<Tenant>> Companies(ClaimsPrincipal principal)
    {
        await using var db = await factory.CreateDbContextAsync();
        if (await IsPlatformAdmin(principal)) return await db.Tenants.OrderBy(x => x.Name).ToListAsync();
        var id = Actor(principal);
        var user = await db.Users.FindAsync(id);
        if (user is null || !user.IsEnabled || user.SecurityStamp != principal.FindFirstValue("security_stamp")) return [];
        return await db.Memberships.Where(x => x.UserId == id && x.Tenant.IsEnabled).Select(x => x.Tenant).OrderBy(x => x.Name).ToListAsync();
    }
}
