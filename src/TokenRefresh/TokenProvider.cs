using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace TokenRefresh;

/// <summary>
/// Hands out a valid access token, refreshing it when it is close to expiry.
///
/// Guarantees:
/// <list type="bullet">
///   <item>Single-flight: if 50 requests notice an expired token at once, the token endpoint is called once.</item>
///   <item>Refresh-token grant first, falling back to client credentials when the refresh token is rejected (invalid_grant).</item>
///   <item>Refresh-token rotation: a new refresh token replaces the old one; if the provider doesn't send one, the old one is kept.</item>
/// </list>
/// </summary>
public sealed class TokenProvider
{
    private readonly HttpClient _tokenClient;
    private readonly OAuthClientOptions _options;
    private readonly ITokenStore _store;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _refreshCount;

    /// <param name="tokenClient">
    /// A plain HttpClient for the token endpoint. Do not pass a client that goes through
    /// <see cref="AuthenticatingHandler"/>, or token requests would try to authenticate themselves.
    /// </param>
    public TokenProvider(HttpClient tokenClient, OAuthClientOptions options, ITokenStore store, TimeProvider? time = null)
    {
        _tokenClient = tokenClient;
        _options = options;
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How many times the token endpoint has been called. Useful for logging and tests.</summary>
    public int RefreshCount => Volatile.Read(ref _refreshCount);

    /// <summary>Raised after every successful refresh (never includes the token value).</summary>
    public event Action<string>? Refreshed;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var current = await _store.GetAsync(cancellationToken);
        if (current is not null && !current.IsExpiringWithin(_options.RefreshSkew, _time.GetUtcNow()))
        {
            return current.AccessToken; // fast path: no lock
        }

        var refreshed = await RefreshAsync(current?.AccessToken, cancellationToken);
        return refreshed.AccessToken;
    }

    /// <summary>
    /// Called when the API rejected <paramref name="rejectedToken"/> with 401 (revoked, clock skew,
    /// password change...). Refreshes unless another caller has already replaced that token.
    /// </summary>
    public async Task<string> RefreshAfterRejectionAsync(string rejectedToken, CancellationToken cancellationToken = default)
    {
        var refreshed = await RefreshAsync(rejectedToken, cancellationToken);
        return refreshed.AccessToken;
    }

    private async Task<TokenSet> RefreshAsync(string? staleAccessToken, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            // Double-checked: while we waited for the lock, someone else may already have refreshed.
            var current = await _store.GetAsync(cancellationToken);
            if (current is not null
                && current.AccessToken != staleAccessToken
                && !current.IsExpiringWithin(_options.RefreshSkew, _time.GetUtcNow()))
            {
                return current;
            }

            TokenSet fresh;
            if (current?.RefreshToken is { } refreshToken)
            {
                try
                {
                    fresh = await RequestTokenAsync(new()
                    {
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = refreshToken,
                    }, previousRefreshToken: refreshToken, cancellationToken);
                    Refreshed?.Invoke("refresh_token");
                }
                catch (OAuthException ex) when (ex.Error == "invalid_grant")
                {
                    // Refresh token expired, revoked or already used: start over.
                    fresh = await RequestClientCredentialsAsync(cancellationToken);
                    Refreshed?.Invoke("client_credentials (refresh token rejected)");
                }
            }
            else
            {
                fresh = await RequestClientCredentialsAsync(cancellationToken);
                Refreshed?.Invoke("client_credentials");
            }

            await _store.SaveAsync(fresh, cancellationToken);
            return fresh;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private Task<TokenSet> RequestClientCredentialsAsync(CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
        if (_options.Scope is not null)
        {
            form["scope"] = _options.Scope;
        }
        return RequestTokenAsync(form, previousRefreshToken: null, cancellationToken);
    }

    private async Task<TokenSet> RequestTokenAsync(
        Dictionary<string, string> form, string? previousRefreshToken, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _refreshCount);
        form["client_id"] = _options.ClientId;
        form["client_secret"] = _options.ClientSecret;

        using var response = await _tokenClient.PostAsync(
            _options.TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            OAuthErrorDto? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<OAuthErrorDto>(cancellationToken);
            }
            catch (System.Text.Json.JsonException)
            {
                // Not an OAuth JSON error body; fall through with a generic error.
            }
            throw new OAuthException(error?.Error ?? "http_error", error?.Description, response.StatusCode);
        }

        var body = await response.Content.ReadFromJsonAsync<TokenResponseDto>(cancellationToken)
                   ?? throw new OAuthException("invalid_response", "Empty token response", response.StatusCode);

        return new TokenSet(
            body.AccessToken,
            body.RefreshToken ?? previousRefreshToken,
            _time.GetUtcNow().AddSeconds(body.ExpiresIn));
    }

    private sealed record TokenResponseDto(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record OAuthErrorDto(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? Description);
}
