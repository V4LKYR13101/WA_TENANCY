using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.EntityFrameworkCore;
using WaTenancy.Web.Data;
namespace WaTenancy.Web.Security;
public sealed class RevalidatingAuthState(ILoggerFactory logger, IDbContextFactory<AppDbContext> factory) : RevalidatingServerAuthenticationStateProvider(logger)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(30);
    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FindAsync([TenantAccess.Actor(state.User)], ct);
        return user is { IsEnabled: true } && user.SecurityStamp == state.User.FindFirst("security_stamp")?.Value;
    }
}
