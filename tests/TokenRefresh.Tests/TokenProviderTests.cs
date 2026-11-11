using System.Net;

namespace TokenRefresh.Tests;

public class TokenProviderTests
{
    private static readonly OAuthClientOptions Options = new()
    {
        TokenEndpoint = new Uri("https://auth.example.com/oauth/token"),
        ClientId = "id",
        ClientSecret = "secret",
        RefreshSkew = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public async Task Concurrent_callers_cause_a_single_token_request()
    {
        var endpoint = new FakeHandler(async _ =>
        {
            await Task.Delay(50); // slow token endpoint makes the race likely
            return FakeHandler.Token("access-1");
        });
        var provider = new TokenProvider(new HttpClient(endpoint), Options, new InMemoryTokenStore());

        var tokens = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => provider.GetAccessTokenAsync()));

        Assert.All(tokens, t => Assert.Equal("access-1", t));
        Assert.Equal(1, endpoint.Calls);
    }

    [Fact]
    public async Task Token_is_refreshed_with_refresh_token_before_it_expires()
    {
        var grants = new List<string>();
        var endpoint = new FakeHandler(async request =>
        {
            var form = await request.Content!.ReadAsStringAsync();
            grants.Add(form.Contains("grant_type=refresh_token") ? "refresh_token" : "client_credentials");
            return FakeHandler.Token($"access-{grants.Count}", refresh: $"refresh-{grants.Count}", expiresIn: 300);
        });
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var provider = new TokenProvider(new HttpClient(endpoint), Options, new InMemoryTokenStore(), clock);

        Assert.Equal("access-1", await provider.GetAccessTokenAsync());

        clock.Advance(TimeSpan.FromSeconds(200)); // 100s left: still fine
        Assert.Equal("access-1", await provider.GetAccessTokenAsync());

        clock.Advance(TimeSpan.FromSeconds(80));  // 20s left: inside the 30s skew
        Assert.Equal("access-2", await provider.GetAccessTokenAsync());

        Assert.Equal(new[] { "client_credentials", "refresh_token" }, grants);
    }

    [Fact]
    public async Task Rejected_refresh_token_falls_back_to_client_credentials()
    {
        var step = 0;
        var endpoint = new FakeHandler(async request =>
        {
            var form = await request.Content!.ReadAsStringAsync();
            step++;
            if (form.Contains("grant_type=refresh_token"))
            {
                return FakeHandler.Json(HttpStatusCode.BadRequest, new { error = "invalid_grant" });
            }
            return FakeHandler.Token($"access-{step}", expiresIn: 1);
        });
        var provider = new TokenProvider(new HttpClient(endpoint), Options, new InMemoryTokenStore());

        await provider.GetAccessTokenAsync();               // client_credentials, expires immediately (1s < 30s skew)
        var token = await provider.GetAccessTokenAsync();   // refresh_token -> invalid_grant -> client_credentials

        Assert.Equal("access-3", token);
        Assert.Equal(3, endpoint.Calls);
    }

    [Fact]
    public async Task Other_oauth_errors_are_surfaced()
    {
        var endpoint = new FakeHandler(_ =>
            Task.FromResult(FakeHandler.Json(HttpStatusCode.Unauthorized, new { error = "invalid_client", error_description = "bad secret" })));
        var provider = new TokenProvider(new HttpClient(endpoint), Options, new InMemoryTokenStore());

        var ex = await Assert.ThrowsAsync<OAuthException>(() => provider.GetAccessTokenAsync());

        Assert.Equal("invalid_client", ex.Error);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }
}
