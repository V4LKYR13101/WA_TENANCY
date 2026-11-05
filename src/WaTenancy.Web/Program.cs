using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WaTenancy.Web.Api;
using WaTenancy.Web.Components;
using WaTenancy.Web.Data;
using WaTenancy.Web.Infrastructure;
using WaTenancy.Web.Security;
using WaTenancy.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();
builder.Host.UseWindowsService(o => o.ServiceName = "WaTenancy");
builder.Services.AddOptions<AppOptions>().Bind(builder.Configuration.GetSection("WaTenancy"))
    .Validate(x => x.MaxTotalWorkers > 0 && x.MaxAccountsPerTenant > 0 && x.MaxConcurrentSends > 0 && x.MaxSendAttempts > 0 && x.MaxConcurrentWebhooks > 0 && x.RetentionDays > 0 && x.CommandTimeoutSeconds >= 5 && x.MaxUploadBytes > 0 && x.MaxUploadBytes <= 64 * 1024 * 1024 && x.SendSpacingMilliseconds >= 0, "Invalid application limits.")
    .Validate(x => Uri.TryCreate(x.InternalBaseUrl, UriKind.Absolute, out var u) && u.Scheme == "http" && (u.Host == "127.0.0.1" || u.Host == "localhost" || u.Host == "[::1]"), "InternalBaseUrl must point to this application's loopback HTTP listener.")
    .ValidateOnStart();
builder.Services.AddOptions<AppOptions>()
    .Validate(x => x.MaxConcurrentMediaDownloads > 0 && x.MaxMediaAttempts > 0 && x.MaxIncomingMediaBytes > 0 && x.MaxIncomingMediaBytes <= 100L * 1024 * 1024 && x.MaxTenantIncomingMediaBytes >= x.MaxIncomingMediaBytes, "Invalid incoming media limits.")
    .Validate(x => string.IsNullOrWhiteSpace(x.PublicBaseUrl) || Uri.TryCreate(x.PublicBaseUrl, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && u.UserInfo.Length == 0 && u.Query.Length == 0 && u.Fragment.Length == 0, "PublicBaseUrl must be an HTTP(S) service base URL.");
builder.Services.AddSingleton<AppPaths>();
builder.Services.AddSingleton<ShortStorePaths>();
builder.Services.AddSingleton<ServerLease>();
builder.Services.AddDbContextFactory<AppDbContext>((sp, o) => o.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sp.GetRequiredService<AppPaths>().Database, DefaultTimeout = 30 }.ToString()));
var protection = builder.Services.AddDataProtection().SetApplicationName("WaTenancy").PersistKeysToFileSystem(new DirectoryInfo(new AppPaths(Microsoft.Extensions.Options.Options.Create(builder.Configuration.GetSection("WaTenancy").Get<AppOptions>() ?? new()), builder.Environment).Keys));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(o =>
{
    o.User.RequireUniqueEmail = true; o.Password.RequiredLength = 12;
    o.Lockout.MaxFailedAccessAttempts = 5; o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
}).AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "WaTenancy.Session"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Configuration.GetValue<bool>("WaTenancy:RequireHttps") ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    o.LoginPath = "/login"; o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = true;
    o.Events.OnValidatePrincipal = async ctx =>
    {
        var factory = ctx.HttpContext.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(TenantAccess.Actor(ctx.Principal!));
        if (user is not { IsEnabled: true } || user.SecurityStamp != ctx.Principal!.FindFirstValue("security_stamp")) ctx.RejectPrincipal();
    };
});
builder.Services.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthentication>(ApiKeyAuthentication.SchemeName, _ => { });
builder.Services.AddAuthorization(); builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingAuthState>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents(o => { o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(1); o.DisconnectedCircuitMaxRetained = 100; });
builder.Services.AddControllersWithViews();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<TenantAccess>(); builder.Services.AddSingleton<TenantService>(); builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<MessageService>(); builder.Services.AddSingleton<WebhookService>(); builder.Services.AddSingleton<IncomingEvents>();
builder.Services.AddSingleton<IncomingMedia>(); builder.Services.AddHostedService<IncomingMediaWorker>();
builder.Services.AddSingleton<WebhookEndpointPolicy>();
builder.Services.AddSingleton<IWacliClient, WacliClient>(); builder.Services.AddSingleton<IWebhookTransport, PublicWebhookTransport>();
builder.Services.AddSingleton<AccountSupervisor>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<AccountSupervisor>());
builder.Services.AddHostedService<MessageDispatcher>(); builder.Services.AddHostedService<WebhookDispatcher>();
builder.Services.AddHostedService<MirrorReconciler>(); builder.Services.AddHostedService<RetentionWorker>();
var requestLimit = builder.Configuration.GetValue<long?>("WaTenancy:MaxUploadBytes") ?? 16 * 1024 * 1024;
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = requestLimit + 2 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = requestLimit + 2 * 1024 * 1024);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("api", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.User.FindFirstValue("tenant_id") ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous", _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.Services.GetRequiredService<ServerLease>();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync(); await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing")) app.UseHsts();
if (app.Configuration.GetValue<bool>("WaTenancy:RequireHttps")) app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/internal") && !ctx.Request.Path.StartsWithSegments("/health"), branch => branch.UseHttpsRedirection());
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff"; ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    ctx.Response.Headers["X-Frame-Options"] = "DENY"; ctx.Response.Headers["Cache-Control"] = "no-cache, no-store"; ctx.Response.Headers["Pragma"] = "no-cache";
    try { await next(); }
    catch (AppException ex) { if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = ex.Status; await ctx.Response.WriteAsJsonAsync(new { title = ex.Message, status = ex.Status }); } }
    catch (System.Text.Json.JsonException) { if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { title = "Invalid JSON payload.", status = 400 }); } }
    catch (BadHttpRequestException ex)
    {
        app.Logger.LogWarning("Invalid HTTP request: status {Status}, method {Method}, path {Path}, trace {TraceId}.", ex.StatusCode, ctx.Request.Method, ctx.Request.Path, ctx.TraceIdentifier);
        if (!ctx.Response.HasStarted) { ctx.Response.StatusCode=ex.StatusCode; await ctx.Response.WriteAsJsonAsync(new { title="Invalid request body or parameters. Check the JSON fields and content type.", status=ex.StatusCode, traceId=ctx.TraceIdentifier }); }
    }
    catch (Exception ex)
    {
        app.Logger.LogError("Request failed ({ExceptionType}), trace {TraceId}.", ex.GetType().Name, ctx.TraceIdentifier);
        if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { title = "An unexpected error occurred.", traceId = ctx.TraceIdentifier }); }
    }
});
app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.UseAntiforgery();
app.MapStaticAssets(); app.MapControllers(); app.MapTenantApi();
app.MapOpenApi();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
public partial class Program { }
