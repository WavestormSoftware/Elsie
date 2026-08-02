using System.Security.Claims;
using Elsie.Auth;

namespace Elsie.Auth.Tests;

/// <summary>Shared test modules + login body for auth integration tests.</summary>
internal static class TestAuth
{
    public sealed record LoginBody(string User, string Password);
}

/// <summary>
/// Login endpoint: <c>ada/pass</c> → authenticated with role <c>admin</c>; <c>bob/pass</c> →
/// authenticated without roles; anything else → 401.
/// </summary>
internal sealed class TestAuthModule : ElsieModule
{
    public TestAuthModule()
    {
        Post("/login", async (ctx, ct) =>
        {
            var body = await ctx.BindJsonAsync<TestAuth.LoginBody>(ct);
            if (!body.IsSuccess || body.Value is null || body.Value.Password != "pass")
            {
                return ElsieResult.Unauthorized("bad credentials");
            }

            var claims = new List<Claim> { new(ClaimTypes.Name, body.Value.User) };
            if (body.Value.User == "ada")
            {
                claims.Add(new Claim(ClaimTypes.Role, "admin"));
            }

            var identity = new ClaimsIdentity(claims, "Cookies");
            await ctx.SignInAsync(new ClaimsPrincipal(identity));
            return ElsieResult.NoContent();
        });

        Post("/logout", async (ctx, _) =>
        {
            await ctx.SignOutAsync();
            return ElsieResult.NoContent();
        });
    }
}

internal sealed class TestSecureModule : ElsieModule
{
    public TestSecureModule()
    {
        Use(ElsieAuthGates.RequireAuthenticated());
        Get("/secure", () => ElsieResult.Text("ok"));
        Get("/me", ctx => ctx.Json(new { name = ctx.GetUser().Identity?.Name }));
    }
}

internal sealed class TestRoleModule : ElsieModule
{
    public TestRoleModule()
    {
        Path("/roles");
        Use(ElsieAuthGates.RequireRole("admin"));
        Get("/admin", () => ElsieResult.Text("admin-ok"));
    }
}
