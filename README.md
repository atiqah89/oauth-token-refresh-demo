# OAuth Token Refresh Demo (.NET 10)

[![CI](https://github.com/atiqah89/oauth-token-refresh-demo/actions/workflows/ci.yml/badge.svg)](https://github.com/atiqah89/oauth-token-refresh-demo/actions/workflows/ci.yml)

A small, dependency-free library that shows how to keep a third-party API integration healthy when OAuth access tokens expire, get revoked early, or the provider starts throttling you.

It covers the problems that cause most integration support tickets:

| Problem | What usually goes wrong | What this library does |
|---|---|---|
| Token expires mid-traffic | 50 parallel requests each call the token endpoint, and the provider rate-limits or invalidates refresh tokens | **Single-flight refresh**: a `SemaphoreSlim` with a double-checked read, so the token endpoint is called once |
| Token expires *in flight* | A request leaves with 1 second left and arrives dead | **Refresh skew**: tokens are refreshed a configurable time before `expires_in` |
| Token revoked early (admin, password change, clock skew) | Integration is "down" until someone restarts it | **401 recovery**: refresh once and replay the request, with no infinite loop |
| Refresh token rotated or expired | `invalid_grant` crashes the sync job | **Fallback** to the client-credentials grant |
| Provider throttles (429/503) | Hammering the API makes the ban longer | **Retry-After aware backoff** with exponential delay and jitter |
| Gateway errors on POST | Blind retries create duplicate bookings | **Idempotency-aware retries**: 502/504 are retried only for GET/PUT/DELETE |

## How it fits together

```
HttpClient
  └─ RetryHandler           429/503 + transient errors, Retry-After, backoff + jitter
       └─ AuthenticatingHandler   adds Bearer token, refresh-and-replay on 401
            └─ HttpClientHandler  network
TokenProvider ── ITokenStore      single-flight refresh, refresh_token → client_credentials fallback
```

`RetryHandler` wraps `AuthenticatingHandler` on purpose, so every retry picks up a currently valid token.

## Run the demo

You need the .NET 10 SDK.

```bash
# terminal 1: a fake SaaS with 10-second tokens, single-use refresh tokens and a 429 on every 5th call
dotnet run --project samples/MockProvider

# terminal 2: 5 concurrent requests every 2 seconds for 40 seconds
dotnet run --project samples/Client
```

What to look for in the output:

- `token endpoint calls so far` grows by **one** per expiry, not by five.
- `retry #1 in 1000 ms (HTTP 429)`: the mock's `Retry-After: 1` is honoured.
- Around round 8 the client revokes every token. The next round recovers on its own through the 401 handler.

## Tests

```bash
dotnet test
```

The tests use a fake `HttpMessageHandler` and a manual `TimeProvider`, so they're fast and don't depend on timing:

- 50 concurrent callers result in exactly 1 token request
- refresh happens inside the skew window, using the refresh-token grant
- `invalid_grant` falls back to client credentials
- a 401 triggers exactly one refresh and replay
- `Retry-After` is honoured, backoff grows, and the handler gives up after `MaxRetries`
- POST is not retried on 502

## Using it in an ASP.NET Core app

```csharp
builder.Services.AddSingleton(new OAuthClientOptions { /* from configuration / Key Vault */ });
builder.Services.AddSingleton<ITokenStore, InMemoryTokenStore>(); // swap for a persistent, encrypted store
builder.Services.AddHttpClient("oauth");                           // plain client for the token endpoint
builder.Services.AddSingleton(sp => new TokenProvider(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("oauth"),
    sp.GetRequiredService<OAuthClientOptions>(),
    sp.GetRequiredService<ITokenStore>()));

builder.Services.AddHttpClient("vendor-api", c => c.BaseAddress = new Uri("https://api.vendor.com"))
    .AddHttpMessageHandler(() => new RetryHandler())
    .AddHttpMessageHandler(sp => new AuthenticatingHandler(sp.GetRequiredService<TokenProvider>()));
```

## Production notes

- **Multi-instance deployments**: the semaphore only covers one process. With several instances sharing one refresh token, use a distributed lock (for example a Redis `SET NX`), or give each instance its own client-credentials token.
- **Multi-tenant**: create one `TokenProvider` per tenant connection, keyed by tenant ID.
- **Never log tokens.** The `Refreshed` event only reports which grant was used.
- **Non-replayable bodies**: the 401 replay needs request content that can be sent twice (`StringContent`, `JsonContent`).

## License

MIT
