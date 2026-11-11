// Calls the mock provider for 40 seconds, 5 requests at a time, and prints what happens.
// Start the provider first:  dotnet run --project samples/MockProvider
using TokenRefresh;

const string baseUrl = "http://localhost:5080";

var options = new OAuthClientOptions
{
    TokenEndpoint = new Uri($"{baseUrl}/oauth/token"),
    ClientId = "demo-client",
    ClientSecret = "demo-secret",
    RefreshSkew = TimeSpan.FromSeconds(2),
};

using var tokenHttp = new HttpClient();
var tokens = new TokenProvider(tokenHttp, options, new InMemoryTokenStore());
tokens.Refreshed += grant => Log($"token refreshed using {grant}");

var retry = new RetryOptions
{
    OnRetry = (attempt, delay, reason) => Log($"retry #{attempt} in {delay.TotalMilliseconds:F0} ms ({reason})"),
};

// Pipeline order matters: Retry wraps Auth, so every retry gets a currently-valid token.
var pipeline = new RetryHandler(retry)
{
    InnerHandler = new AuthenticatingHandler(tokens) { InnerHandler = new HttpClientHandler() },
};
using var api = new HttpClient(pipeline) { BaseAddress = new Uri(baseUrl) };

var stopAt = DateTime.UtcNow.AddSeconds(40);
var round = 0;
while (DateTime.UtcNow < stopAt)
{
    round++;
    if (round == 8)
    {
        Log("simulating early revocation (POST /admin/revoke-all)");
        using var revoke = await tokenHttp.PostAsync($"{baseUrl}/admin/revoke-all", null);
    }

    var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => api.GetAsync("/api/meetings")));
    Log($"round {round,2}: statuses [{string.Join(", ", responses.Select(r => (int)r.StatusCode))}]  " +
        $"token endpoint calls so far: {tokens.RefreshCount}");
    foreach (var response in responses)
    {
        response.Dispose();
    }

    await Task.Delay(TimeSpan.FromSeconds(2));
}

static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {message}");
