using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WaTenancy.Web.Data;

namespace WaTenancy.Web.Controllers;

[Route("auth"), EnableRateLimiting("login")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class AuthController(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, IDbContextFactory<AppDbContext> factory) : Controller
{
    private static readonly SemaphoreSlim SetupLock = new(1, 1);
    [HttpPost("login"), ValidateAntiForgeryToken, AllowAnonymous]
    public async Task<IActionResult> Login([FromForm] string email, [FromForm] string password, [FromForm] string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password)) return BadRequest();
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null || !user.IsEnabled) return LocalRedirect("/login?error=invalid");
        var check = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (!check.Succeeded) return LocalRedirect("/login?error=invalid");
        await signIn.SignInWithClaimsAsync(user, isPersistent: false, [new Claim("security_stamp", user.SecurityStamp ?? "")]);
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }
    [HttpPost("logout"), ValidateAntiForgeryToken, Authorize]
    public async Task<IActionResult> Logout() { await signIn.SignOutAsync(); return LocalRedirect("/login"); }
    [HttpPost("setup"), ValidateAntiForgeryToken, AllowAnonymous]
    public async Task<IActionResult> Setup([FromForm] string email, [FromForm] string password, [FromForm] string confirmPassword)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password)) return BadRequest();
        if (HttpContext.Connection.RemoteIpAddress is { } ip && !IPAddress.IsLoopback(ip)) return StatusCode(403);
        await SetupLock.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            if (await db.Users.AnyAsync()) return NotFound();
            if (password != confirmPassword) return LocalRedirect("/setup?error=match");
            email = email.Trim().ToLowerInvariant();
            if (!new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email)) return LocalRedirect("/setup?error=email");
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, IsPlatformAdmin = true };
            var result = await users.CreateAsync(user, password);
            if (!result.Succeeded) return LocalRedirect("/setup?error=password");
            await signIn.SignInWithClaimsAsync(user, false, [new Claim("security_stamp", user.SecurityStamp ?? "")]);
            return LocalRedirect("/");
        }
        finally { SetupLock.Release(); }
    }
}
