using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WaTenancy.Web.Data;
using WaTenancy.Web.Security;
namespace WaTenancy.Web.Services;
public sealed record UserView(string Id, string Email, bool Enabled, bool PlatformAdmin);
public sealed class UserService(IServiceScopeFactory scopes, IDbContextFactory<AppDbContext> factory, TenantAccess access)
{
    public async Task ChangePassword(ClaimsPrincipal p, string current, string next)
    {
        using var scope = scopes.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(TenantAccess.Actor(p)) ?? throw new AppException(401, "Sign in again.");
        if (!user.IsEnabled || user.SecurityStamp != p.FindFirstValue("security_stamp")) throw new AppException(401, "Sign in again.");
        var result = await users.ChangePasswordAsync(user, current, next);
        if (!result.Succeeded) throw new AppException(400, string.Join(" ", result.Errors.Select(x => x.Description)));
    }
    public async Task<List<UserView>> List(ClaimsPrincipal p)
    {
        await access.RequirePlatformAdmin(p); await using var db = await factory.CreateDbContextAsync();
        return await db.Users.OrderBy(x => x.Email).Select(x => new UserView(x.Id, x.Email!, x.IsEnabled, x.IsPlatformAdmin)).ToListAsync();
    }
    public async Task SetEnabled(ClaimsPrincipal p, string id, bool enabled)
    {
        await access.RequirePlatformAdmin(p);
        if (TenantAccess.Actor(p) == id) throw new AppException(409, "You cannot disable your own administrator account.");
        using var scope = scopes.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(id) ?? throw new AppException(404, "User not found.");
        user.IsEnabled = enabled; await users.UpdateAsync(user); await users.UpdateSecurityStampAsync(user);
        await using var db = await factory.CreateDbContextAsync(); TenantService.Audit(db, p, null, enabled ? "user.enabled" : "user.disabled", id); await db.SaveChangesAsync();
    }
    public async Task ResetPassword(ClaimsPrincipal p, string id, string password)
    {
        await access.RequirePlatformAdmin(p); using var scope = scopes.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(id) ?? throw new AppException(404, "User not found.");
        var token = await users.GeneratePasswordResetTokenAsync(user); var result = await users.ResetPasswordAsync(user, token, password);
        if (!result.Succeeded) throw new AppException(400, string.Join(" ", result.Errors.Select(x => x.Description)));
        await using var db = await factory.CreateDbContextAsync(); TenantService.Audit(db, p, null, "user.password_reset", id); await db.SaveChangesAsync();
    }
}
