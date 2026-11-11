using System.Net;

namespace TokenRefresh;

/// <summary>Settings for an OAuth 2.0 client (client credentials + refresh token grants).</summary>
public sealed class OAuthClientOptions
{
    public required Uri TokenEndpoint { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public string? Scope { get; init; }

    /// <summary>
    /// Refresh this long before the token actually expires, so a request that
    /// starts just before expiry doesn't arrive at the API with a dead token.
    /// </summary>
    public TimeSpan RefreshSkew { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>An access token plus the refresh token and absolute expiry that came with it.</summary>
public sealed record TokenSet(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt)
{
    public bool IsExpiringWithin(TimeSpan skew, DateTimeOffset now) => now + skew >= ExpiresAt;
}

/// <summary>Raised when the token endpoint returns an OAuth error such as invalid_grant.</summary>
public sealed class OAuthException(string error, string? description, HttpStatusCode statusCode)
    : Exception($"OAuth error '{error}' ({(int)statusCode}): {description}")
{
    public string Error { get; } = error;
    public string? Description { get; } = description;
    public HttpStatusCode StatusCode { get; } = statusCode;
}
