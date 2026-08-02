using System.Net;
using System.Security.Claims;
using Elsie.Auth;
using Elsie.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Auth.Tests;

/// <summary>
/// Policy gates validate policy names against the ambient <see cref="ElsieAuthOptions"/> at gate
/// creation, so these tests run serially against the rest of the assembly.
/// </summary>
[CollectionDefinition("AuthSerial", DisableParallelization = true)]
public sealed class AuthSerialCollection
{
}

[Collection("AuthSerial")]
public class PolicyTests
{
    private sealed class PolicyModule : ElsieModule
    {
        public PolicyModule(ElsieAuthOptions auth)
        {
            Path("/policy");
            Before(ElsieAuthGates.RequirePolicy(auth, "admin"));
            Get("/admin", () => ElsieResult.Text("policy-ok"));
        }
    }

    private sealed class BadPolicyModule : ElsieModule
    {
        public BadPolicyModule(ElsieAuthOptions auth)
        {
            Before(ElsieAuthGates.RequirePolicy(auth, "not-registered"));
            Get("/x", () => ElsieResult.Text("x"));
        }
    }

    private static void Configure(ElsieAuthOptions o)
    {
        o.Cookie = new ElsieCookieAuthOptions { CookieName = "t", Secure = false };
        o.Cookie.TicketKeyFromString("test-ticket-key!!");
        o.AddElsiePolicy("admin", p => p.RequireRole("admin"));
        o.AddElsiePolicy("auditor", p => p.RequireClaim(ClaimTypes.Name, "ada").RequireRole("admin"));
    }

    [Fact]
    public async Task Policy_gate_allows_matching_principal()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(Configure);
            s.AddElsieModule<TestAuthModule>();
            s.AddElsieModule<PolicyModule>();
        });

        // Anonymous → challenge (401).
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetAsync("/policy/admin")).StatusCode);

        // ada has the admin role → passes the "admin" policy.
        (await host.PostJsonAsync("/login", new TestAuth.LoginBody("ada", "pass"))).EnsureSuccessStatusCode();
        Assert.Equal("policy-ok", await host.Client.GetStringAsync("/policy/admin"));
    }

    [Fact]
    public async Task Policy_gate_denies_principal_missing_requirements()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(Configure);
            s.AddElsieModule<TestAuthModule>();
            s.AddElsieModule<PolicyModule>();
        });

        // bob is authenticated but has no admin role → 403.
        (await host.PostJsonAsync("/login", new TestAuth.LoginBody("bob", "pass"))).EnsureSuccessStatusCode();
        var res = await host.GetAsync("/policy/admin");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Composite_policy_requires_all_requirements()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(o =>
            {
                Configure(o);
                o.AddElsiePolicy("strict", p => p.RequireRole("admin").RequireClaim(ClaimTypes.Name, "ada"));
            });
            s.AddElsieModule<TestAuthModule>();
            s.AddElsieModule<StrictPolicyModule>();
        });

        (await host.PostJsonAsync("/login", new TestAuth.LoginBody("ada", "pass"))).EnsureSuccessStatusCode();
        Assert.Equal("strict-ok", await host.Client.GetStringAsync("/policy/strict"));

        (await host.PostJsonAsync("/login", new TestAuth.LoginBody("bob", "pass"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/policy/strict")).StatusCode);
    }

    private sealed class StrictPolicyModule : ElsieModule
    {
        public StrictPolicyModule(ElsieAuthOptions auth)
        {
            Path("/policy");
            Before(ElsieAuthGates.RequirePolicy(auth, "strict"));
            Get("/strict", () => ElsieResult.Text("strict-ok"));
        }
    }

    [Fact]
    public void Unknown_policy_throws_at_startup()
    {
        Assert.Throws<InvalidOperationException>(() => ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(Configure);
            s.AddElsieModule<BadPolicyModule>();
        }));
    }

    [Fact]
    public void Duplicate_policy_registration_throws()
    {
        var options = new ElsieAuthOptions();
        options.AddElsiePolicy("dup", p => p.RequireRole("admin"));
        Assert.Throws<InvalidOperationException>(() => options.AddElsiePolicy("dup", p => p.RequireRole("admin")));
    }

    [Fact]
    public void One_arg_RequirePolicy_validates_against_ambient_options()
    {
        var services = new ServiceCollection();
        services.AddElsieAuth(o =>
        {
            o.Cookie = new ElsieCookieAuthOptions { CookieName = "t", Secure = false };
            o.Cookie.TicketKeyFromString("test-ticket-key!!");
            o.AddElsiePolicy("unique-policy-xyz", p => p.RequireRole("admin"));
        });

        // Registered name: gate created without throwing.
        var gate = ElsieAuthGates.RequirePolicy("unique-policy-xyz");
        Assert.NotNull(gate);

        // Unknown name fails fast at gate creation (startup exception).
        Assert.Throws<InvalidOperationException>(() => ElsieAuthGates.RequirePolicy("unique-nope-xyz"));
    }
}
