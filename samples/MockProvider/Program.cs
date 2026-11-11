// A tiny fake "third-party SaaS" with an OAuth token endpoint and one API.
// It behaves like real providers do on a bad day:
//   * access tokens live only 10 seconds
//   * refresh tokens are single-use (rotation)
//   * every 5th API call is rate-limited with 429 + Retry-After
//   * POST /admin/revoke-all kills every token early, to show 401 recovery
using System.Collections.Concurrent;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var lifetime = TimeSpan.FromSeconds(app.Configuration.GetValue("Mock:TokenLifetimeSeconds", 10));
var accessTokens = new ConcurrentDictionary<string, DateTimeOffset>();
var refreshTokens = new ConcurrentDictionary<string, byte>();
var apiCalls = new int[1];

static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

app.MapPost("/oauth/token", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    if (form["client_id"] != "demo-client" || form["client_secret"] != "demo-secret")
    {
        return Results.Json(new { error = "invalid_client" }, statusCode: 401);
    }

    switch (form["grant_type"].ToString())
    {
        case "client_credentials":
            break;
        case "refresh_token":
            if (!refreshTokens.TryRemove(form["refresh_token"].ToString(), out _))
            {
                return Results.Json(
                    new { error = "invalid_grant", error_description = "Refresh token is unknown or was already used." },
                    statusCode: 400);
            }
            break;
        default:
            return Results.Json(new { error = "unsupported_grant_type" }, statusCode: 400);
    }

    var access = NewToken();
    var refresh = NewToken();
    accessTokens[access] = DateTimeOffset.UtcNow + lifetime;
    refreshTokens[refresh] = 0;
    app.Logger.LogInformation("Issued token via {Grant}", form["grant_type"].ToString());

    return Results.Json(new
    {
        access_token = access,
        token_type = "Bearer",
        expires_in = (int)lifetime.TotalSeconds,
        refresh_token = refresh,
    });
});

app.MapGet("/api/meetings", (HttpContext context) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : "";
    if (!accessTokens.TryGetValue(token, out var expiresAt) || expiresAt < DateTimeOffset.UtcNow)
    {
        return Results.Json(new { error = "invalid_token" }, statusCode: 401);
    }

    if (Interlocked.Increment(ref apiCalls[0]) % 5 == 0)
    {
        context.Response.Headers.RetryAfter = "1";
        return Results.Json(new { error = "rate_limited" }, statusCode: 429);
    }

    return Results.Ok(new[]
    {
        new { id = 1, room = "Kuala Lumpur 12F - Boardroom", startsAt = DateTimeOffset.UtcNow.AddHours(1) },
        new { id = 2, room = "Penang Hub - Huddle 3", startsAt = DateTimeOffset.UtcNow.AddHours(3) },
    });
});

app.MapPost("/admin/revoke-all", () =>
{
    accessTokens.Clear();
    return Results.NoContent();
});

app.Run("http://localhost:5080");
