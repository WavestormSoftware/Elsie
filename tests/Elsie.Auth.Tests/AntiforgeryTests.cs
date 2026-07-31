using System.Text;
using Elsie.Auth;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Auth.Tests;

public class AntiforgeryTests
{
    [Fact]
    public void Token_roundtrip_header_validates()
    {
        var services = new ServiceCollection();
        services.AddElsieAntiforgery(o => o.SigningKey = new byte[32]);
        var sp = services.BuildServiceProvider();
        var svc = sp.GetRequiredService<ElsieAntiforgeryService>();

        var response = new ElsieResponse();
        var ctx = new ElsieContext(
            new ElsieRequest("GET", "/", requestServices: sp),
            response,
            new Dictionary<string, string>());
        var token = svc.GetAndStoreToken(ctx);
        Assert.False(string.IsNullOrEmpty(token));
        Assert.NotEmpty(response.SetCookies);

        var headers = new Dictionary<string, string>
        {
            ["Cookie"] = $"elsie-csrf={Uri.EscapeDataString(token)}",
            ["X-CSRF-TOKEN"] = token
        };
        var ctx2 = new ElsieContext(
            new ElsieRequest("POST", "/x", headers: headers, requestServices: sp),
            new ElsieResponse(),
            new Dictionary<string, string>());
        Assert.True(svc.IsValid(ctx2));
    }

    [Fact]
    public async Task Token_roundtrip_form_field_validates_and_body_reusable()
    {
        var services = new ServiceCollection();
        services.AddElsieAntiforgery(o => o.SigningKey = new byte[32]);
        var sp = services.BuildServiceProvider();
        var svc = sp.GetRequiredService<ElsieAntiforgeryService>();

        var issue = new ElsieContext(
            new ElsieRequest("GET", "/", requestServices: sp),
            new ElsieResponse(),
            new Dictionary<string, string>());
        var token = svc.GetAndStoreToken(issue);

        var bodyText = $"Email=ada%40elsie.dev&Password=pass&__RequestVerificationToken={Uri.EscapeDataString(token)}";
        var bodyBytes = Encoding.UTF8.GetBytes(bodyText);
        var headers = new Dictionary<string, string>
        {
            ["Cookie"] = $"elsie-csrf={Uri.EscapeDataString(token)}",
            ["Content-Type"] = "application/x-www-form-urlencoded"
        };
        var ctx = new ElsieContext(
            new ElsieRequest(
                "POST",
                "/login",
                headers: headers,
                body: new MemoryStream(bodyBytes),
                contentLength: bodyBytes.Length,
                contentType: "application/x-www-form-urlencoded",
                requestServices: sp),
            new ElsieResponse(),
            new Dictionary<string, string>());

        Assert.True(await svc.IsValidAsync(ctx));

        var bind = await ctx.BindFormAsync<LoginForm>();
        Assert.True(bind.IsSuccess);
        Assert.Equal("ada@elsie.dev", bind.Value!.Email);
        Assert.Equal("pass", bind.Value.Password);
    }

    [Fact]
    public void Missing_token_fails()
    {
        var services = new ServiceCollection();
        services.AddElsieAntiforgery(o => o.SigningKey = new byte[32]);
        var sp = services.BuildServiceProvider();
        var svc = sp.GetRequiredService<ElsieAntiforgeryService>();
        var ctx = new ElsieContext(
            new ElsieRequest("POST", "/x", requestServices: sp),
            new ElsieResponse(),
            new Dictionary<string, string>());
        Assert.False(svc.IsValid(ctx));
    }

    private sealed class LoginForm
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
    }
}
