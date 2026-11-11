using System.Net;
using System.Net.Http.Headers;

namespace TokenRefresh;

/// <summary>
/// Adds the bearer token to every request. If the API still answers 401
/// (token revoked early, clock skew), it refreshes once and replays the request.
/// </summary>
public sealed class AuthenticatingHandler(TokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokens.GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        // Replay once with a fresh token. Request content must be replayable
        // (StringContent / JsonContent are; a forward-only stream is not).
        response.Dispose();
        var freshToken = await tokens.RefreshAfterRejectionAsync(token, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", freshToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
