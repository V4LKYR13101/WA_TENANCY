using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WaTenancy.Web.Data;
using WaTenancy.Web.Infrastructure;
using WaTenancy.Web.Security;

namespace WaTenancy.Web.Services;

public sealed record MemberView(string UserId, string Email, TenantRole Role);
public sealed record KeyView(Guid Id, string Name, string Prefix, bool CanSend, bool CanManageAccounts, DateTime CreatedAt, DateTime? ExpiresAt, DateTime? RevokedAt);
public sealed record DashboardStats(int Accounts, int Connected, int Queued, int Failed, int Received, int WebhookPending);
public sealed record WebhookProfile(string? Url, bool Enabled, bool HasSecret);

public sealed class TenantService(IDbContextFactory<AppDbContext> factory, TenantAccess access, IDataProtectionProvider protection, IOptions<AppOptions> options, IServiceScopeFactory scopes, WebhookEndpointPolicy webhookEndpoints)
{
    private readonly IDataProtector protector = protection.CreateProtector("WaTenancy.Secrets.v1");
    public static string Name(string? value, int max = 100)
    {
        var result = value?.Trim() ?? "";
        if (result.Length is < 2 || result.Length > max) throw new AppException(400, $"Enter between 2 and {max} characters.");
        return result;
    }
    public static void Audit(AppDbContext db, ClaimsPrincipal p, Guid? tenant, string action, string detail = "") => db.Audit.Add(new AuditEntry { TenantId = tenant, Actor = TenantAccess.Actor(p), Action = action, Detail = detail });
    public async Task<Tenant> CreateCompany(ClaimsPrincipal p, string name, string slug)
    {
        await access.RequirePlatformAdmin(p);
        name = Name(name); slug = (slug ?? "").Trim().ToLowerInvariant();
        if (!Regex.IsMatch(slug, "^[a-z0-9][a-z0-9-]{1,49}$")) throw new AppException(400, "Company slug must be 2–50 lowercase letters, digits or hyphens.");
        await using var db = await factory.CreateDbContextAsync();
        if (await db.Tenants.AnyAsync(x => x.Slug == slug)) throw new AppException(409, "This company slug is already in use.");
        var tenant = new Tenant { Name = name, Slug = slug, ProtectedWebhookSecret = protector.Protect(SecretTokens.Generate()) };
        db.Tenants.Add(tenant); Audit(db, p, tenant.Id, "company.created", name);
        await db.SaveChangesAsync(); return tenant;
    }
    public async Task SetCompanyEnabled(ClaimsPrincipal p, Guid id, bool enabled)
    {
        await access.RequirePlatformAdmin(p);
        await using var db = await factory.CreateDbContextAsync();
        var tenant = await db.Tenants.FindAsync(id) ?? throw new AppException(404, "Company not found.");
        tenant.IsEnabled = enabled; Audit(db, p, id, enabled ? "company.enabled" : "company.disabled");
        await db.SaveChangesAsync();
    }
    public async Task<List<WhatsAppAccount>> Accounts(ClaimsPrincipal p, Guid tenantId)
    {
        await access.Require(p, tenantId);
        await using var db = await factory.CreateDbContextAsync();
        return await db.Accounts.Where(x => x.TenantId == tenantId).OrderBy(x => x.Name).Select(x => new WhatsAppAccount { Id = x.Id, TenantId = x.TenantId, Name = x.Name, Phone = x.Phone, WatchEnabled = x.WatchEnabled, State = x.State, LastError = x.LastError, LastConnectedAt = x.LastConnectedAt, CreatedAt = x.CreatedAt }).ToListAsync();
    }
    public async Task<WhatsAppAccount> CreateAccount(ClaimsPrincipal p, Guid tenantId, string name)
    {
        await access.Require(p, tenantId, TenantRole.Admin, "accounts"); name = Name(name);
        await using var db = await factory.CreateDbContextAsync();
        if (await db.Accounts.CountAsync(x => x.TenantId == tenantId) >= options.Value.MaxAccountsPerTenant) throw new AppException(409, "This company has reached its account limit.");
        if (await db.Accounts.AnyAsync(x => x.TenantId == tenantId && x.Name == name)) throw new AppException(409, "An account with that name already exists.");
        var account = new WhatsAppAccount { TenantId = tenantId, Name = name, ProtectedWebhookSecret = protector.Protect(SecretTokens.Generate()) };
        db.Accounts.Add(account); Audit(db, p, tenantId, "account.created", account.Id.ToString());
        await db.SaveChangesAsync(); account.ProtectedWebhookSecret = ""; return account;
    }
    public async Task<DashboardStats> Stats(ClaimsPrincipal p, Guid id)
    {
        await access.Require(p, id);
        await using var db = await factory.CreateDbContextAsync();
        return new(await db.Accounts.CountAsync(x => x.TenantId == id), await db.Accounts.CountAsync(x => x.TenantId == id && x.State == AccountState.Connected), await db.Jobs.CountAsync(x => x.TenantId == id && x.State == JobState.Queued), await db.Jobs.CountAsync(x => x.TenantId == id && (x.State == JobState.Failed || x.State == JobState.Unknown)), await db.Inbox.CountAsync(x => x.TenantId == id && !x.FromMe), await db.WebhookDeliveries.CountAsync(x => x.TenantId == id && x.State == "Pending"));
    }
    public async Task<List<MemberView>> Members(ClaimsPrincipal p, Guid id)
    {
        await access.Require(p, id, TenantRole.Admin);
        await using var db = await factory.CreateDbContextAsync();
        return await db.Memberships.Where(x => x.TenantId == id).Select(x => new MemberView(x.UserId, x.User.Email!, x.Role)).ToListAsync();
    }
    public async Task AddMember(ClaimsPrincipal p, Guid id, string email, string? initialPassword, TenantRole role)
    {
        var actorRole = await access.Require(p, id, TenantRole.Admin);
        if (!Enum.IsDefined(role) || role == TenantRole.Owner && actorRole != TenantRole.Owner) throw new AppException(403, "Only an owner can grant owner access.");
        email = email.Trim().ToLowerInvariant();
        if (email.Length > 254 || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email)) throw new AppException(400, "Enter a valid email address.");
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            if (string.IsNullOrEmpty(initialPassword)) throw new AppException(400, "An initial password is required for a new user.");
            user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            var result = await users.CreateAsync(user, initialPassword);
            if (!result.Succeeded) throw new AppException(400, string.Join(" ", result.Errors.Select(x => x.Description)));
        }
        await using var db = await factory.CreateDbContextAsync();
        if (await db.Memberships.AnyAsync(x => x.TenantId == id && x.UserId == user.Id)) throw new AppException(409, "This user already belongs to the company.");
        db.Memberships.Add(new Membership { TenantId = id, UserId = user.Id, Role = role });
        Audit(db, p, id, "member.added", user.Id); await db.SaveChangesAsync();
    }
    public async Task RemoveMember(ClaimsPrincipal p, Guid id, string userId)
    {
        var actorRole = await access.Require(p, id, TenantRole.Admin);
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var member = await db.Memberships.FindAsync(id, userId) ?? throw new AppException(404, "Member not found.");
        if (member.Role == TenantRole.Owner && actorRole != TenantRole.Owner) throw new AppException(403, "Only an owner can remove an owner.");
        if (member.Role == TenantRole.Owner && await db.Memberships.CountAsync(x => x.TenantId == id && x.Role == TenantRole.Owner) <= 1) throw new AppException(409, "Add another owner before removing the last owner.");
        db.Memberships.Remove(member); Audit(db, p, id, "member.removed", userId); await db.SaveChangesAsync(); await transaction.CommitAsync();
    }
    public async Task<List<KeyView>> Keys(ClaimsPrincipal p, Guid id)
    {
        await access.Require(p, id, TenantRole.Admin);
        await using var db = await factory.CreateDbContextAsync();
        return await db.ApiKeys.Where(x => x.TenantId == id).OrderByDescending(x => x.CreatedAt).Select(x => new KeyView(x.Id, x.Name, x.Prefix, x.CanSend, x.CanManageAccounts, x.CreatedAt, x.ExpiresAt, x.RevokedAt)).ToListAsync();
    }
    public async Task<string> CreateKey(ClaimsPrincipal p, Guid id, string name, bool send, bool manage, int days)
    {
        await access.Require(p, id, TenantRole.Admin); name = Name(name);
        if (days is < 1 or > 365) throw new AppException(400, "API keys must expire within 1–365 days.");
        var token = "wat_" + SecretTokens.Generate();
        await using var db = await factory.CreateDbContextAsync();
        db.ApiKeys.Add(new ApiKey { TenantId = id, Name = name, Prefix = token[..12], Hash = SecretTokens.Hash(token), CanSend = send, CanManageAccounts = manage, ExpiresAt = DateTime.UtcNow.AddDays(days) });
        Audit(db, p, id, "api_key.created", name); await db.SaveChangesAsync(); return token;
    }
    public async Task RevokeKey(ClaimsPrincipal p, Guid id, Guid keyId)
    {
        await access.Require(p, id, TenantRole.Admin);
        await using var db = await factory.CreateDbContextAsync();
        var key = await db.ApiKeys.SingleOrDefaultAsync(x => x.TenantId == id && x.Id == keyId) ?? throw new AppException(404, "Key not found.");
        key.RevokedAt = DateTime.UtcNow; Audit(db, p, id, "api_key.revoked", keyId.ToString()); await db.SaveChangesAsync();
    }
    public async Task<List<AuditEntry>> AuditLog(ClaimsPrincipal p, Guid id)
    {
        await access.Require(p, id, TenantRole.Admin);
        await using var db = await factory.CreateDbContextAsync();
        return await db.Audit.Where(x => x.TenantId == id).OrderByDescending(x => x.Timestamp).Take(100).ToListAsync();
    }
    public async Task<WebhookProfile> Webhook(ClaimsPrincipal p, Guid id)
    {
        await access.Require(p, id, TenantRole.Admin);
        await using var db = await factory.CreateDbContextAsync();
        var tenant = await db.Tenants.FindAsync(id) ?? throw new AppException(404, "Company not found.");
        return new(tenant.WebhookUrl, tenant.WebhookEnabled, tenant.ProtectedWebhookSecret.Length > 0);
    }
    public async Task SaveWebhook(ClaimsPrincipal p, Guid id, string? url, bool enabled)
    {
        await access.Require(p, id, TenantRole.Admin);
        if (!string.IsNullOrWhiteSpace(url)) { await webhookEndpoints.Validate(url); url = url.Trim(); }
        else if (enabled) throw new AppException(400, "Set a webhook URL before enabling delivery.");
        await using var db = await factory.CreateDbContextAsync();
        var tenant = await db.Tenants.FindAsync(id) ?? throw new AppException(404, "Company not found.");
        tenant.WebhookUrl = string.IsNullOrWhiteSpace(url) ? null : url; tenant.WebhookEnabled = enabled;
        Audit(db, p, id, "webhook.updated"); await db.SaveChangesAsync();
    }
    public async Task<string> RotateWebhookSecret(ClaimsPrincipal p, Guid id)
    {
        await access.Require(p, id, TenantRole.Admin);
        await using var db = await factory.CreateDbContextAsync();
        var tenant = await db.Tenants.FindAsync(id) ?? throw new AppException(404, "Company not found.");
        var secret = SecretTokens.Generate(); tenant.ProtectedWebhookSecret = protector.Protect(secret);
        Audit(db, p, id, "webhook.secret_rotated"); await db.SaveChangesAsync(); return secret;
    }
}
