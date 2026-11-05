using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WaTenancy.Web.Services;

namespace WaTenancy.Tests;
public sealed class AuthTests
{
    private static string Token(string html) => Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
    [Fact] public async Task FirstRunSetupCreatesAdminAndThenCloses()
    {
        await using var app = new TestApplication(); using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var setup = await client.GetStringAsync("/setup"); var token = Token(setup); Assert.NotEmpty(token);
        var response = await client.PostAsync("/auth/setup", new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = "first@example.test", ["password"] = "TestPassword!2026", ["confirmPassword"] = "TestPassword!2026", ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); Assert.Equal("/", response.Headers.Location!.ToString());
        Assert.Contains("Company workspace", await client.GetStringAsync("/")); Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/setup")).StatusCode);
    }
    [Fact] public async Task LoginNeedsAntiforgeryAndCookieDoesNotAuthenticateApi()
    {
        await using var app = new TestApplication(); await app.Seed(); using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var values = new Dictionary<string, string> { ["email"] = "admin@example.test", ["password"] = "TestPassword!2026", ["returnUrl"] = "//evil.example" };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/login", new FormUrlEncodedContent(values))).StatusCode);
        var login = await client.GetStringAsync("/login"); values["__RequestVerificationToken"] = Token(login);
        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(values)); Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); Assert.Equal("/", response.Headers.Location!.ToString());
        Assert.Contains("Company workspace", await client.GetStringAsync("/")); Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/accounts")).StatusCode);
    }
    [Fact] public async Task PasswordChangeInvalidatesExistingSession()
    {
        await using var app = new TestApplication(); var seed = await app.Seed();
        await app.Services.GetRequiredService<UserService>().ChangePassword(seed.Viewer, "TestPassword!2026", "NewTestPassword!2026");
        var exception = await Assert.ThrowsAsync<WaTenancy.Web.Security.AppException>(() => app.Services.GetRequiredService<TenantService>().Accounts(seed.Viewer, seed.TenantA)); Assert.Equal(401, exception.Status);
    }
}
