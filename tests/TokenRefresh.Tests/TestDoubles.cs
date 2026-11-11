using System.Net;
using System.Text;
using System.Text.Json;

namespace TokenRefresh.Tests;

/// <summary>An HttpMessageHandler whose behaviour is a lambda.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public int Calls;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return respond(request);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Token(string access, string? refresh = "refresh-1", int expiresIn = 3600) =>
        Json(HttpStatusCode.OK, new { access_token = access, refresh_token = refresh, expires_in = expiresIn, token_type = "Bearer" });
}

/// <summary>A clock the test can move forward.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
