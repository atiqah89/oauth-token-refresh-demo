using System.Net;
using System.Net.Http.Headers;

namespace TokenRefresh.Tests;

public class HandlerTests
{
    private static readonly OAuthClientOptions Options = new()
    {
        TokenEndpoint = new Uri("https://auth.example.com/oauth/token"),
        ClientId = "id",
        ClientSecret = "secret",
    };

    [Fact]
    public async Task Unauthorized_response_triggers_one_refresh_and_replay()
    {
        var issued = 0;
        var tokenEndpoint = new FakeHandler(_ => Task.FromResult(FakeHandler.Token($"t{Interlocked.Increment(ref issued)}")));
        var provider = new TokenProvider(new HttpClient(tokenEndpoint), Options, new InMemoryTokenStore());

        var seen = new List<string>();
        var api = new FakeHandler(request =>
        {
            var token = request.Headers.Authorization!.Parameter!;
            seen.Add(token);
            // The API revoked t1 early; only t2 works.
            return Task.FromResult(new HttpResponseMessage(token == "t2" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized));
        });
        using var client = new HttpClient(new AuthenticatingHandler(provider) { InnerHandler = api });

        using var response = await client.GetAsync("https://api.example.com/things");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "t1", "t2" }, seen);
    }

    [Fact]
    public async Task Persistent_unauthorized_is_returned_after_one_replay()
    {
        var tokenEndpoint = new FakeHandler(_ => Task.FromResult(FakeHandler.Token(Guid.NewGuid().ToString())));
        var provider = new TokenProvider(new HttpClient(tokenEndpoint), Options, new InMemoryTokenStore());
        var api = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var client = new HttpClient(new AuthenticatingHandler(provider) { InnerHandler = api });

        using var response = await client.GetAsync("https://api.example.com/things");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(2, api.Calls); // no infinite loop
    }

    [Fact]
    public async Task Retry_after_header_is_honoured()
    {
        var delays = new List<TimeSpan>();
        var api = new FakeHandler(_ =>
        {
            if (delays.Count == 0)
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return Task.FromResult(throttled);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var retry = new RetryOptions { Delay = (d, _) => { delays.Add(d); return Task.CompletedTask; } };
        using var client = new HttpClient(new RetryHandler(retry) { InnerHandler = api });

        using var response = await client.GetAsync("https://api.example.com/things");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, delays);
    }

    [Fact]
    public async Task Gives_up_after_max_retries_with_growing_backoff()
    {
        var delays = new List<TimeSpan>();
        var api = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var retry = new RetryOptions
        {
            MaxRetries = 3,
            BaseDelay = TimeSpan.FromMilliseconds(100),
            Delay = (d, _) => { delays.Add(d); return Task.CompletedTask; },
        };
        using var client = new HttpClient(new RetryHandler(retry) { InnerHandler = api });

        using var response = await client.GetAsync("https://api.example.com/things");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(4, api.Calls);
        Assert.Equal(3, delays.Count);
        Assert.True(delays[0] < delays[1] && delays[1] < delays[2], "backoff should grow");
    }

    [Fact]
    public async Task Post_is_not_retried_on_bad_gateway()
    {
        var api = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        var retry = new RetryOptions { Delay = (_, _) => Task.CompletedTask };
        using var client = new HttpClient(new RetryHandler(retry) { InnerHandler = api });

        using var response = await client.PostAsync("https://api.example.com/bookings", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, api.Calls); // the booking may have been created; retrying could double-book
    }
}
