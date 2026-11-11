using System.Net;

namespace TokenRefresh;

public sealed class RetryOptions
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Called before each retry with (attempt number, delay, reason).</summary>
    public Action<int, TimeSpan, string>? OnRetry { get; init; }

    /// <summary>Swappable so tests can record delays instead of sleeping.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (delay, ct) => Task.Delay(delay, ct);

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// Retries throttled and transient failures with exponential backoff + jitter,
/// honouring the provider's Retry-After header when it sends one.
///
/// 429 and 503 mean the server did not process the request, so any method is retried.
/// 502/504 and network errors might have reached the server, so only idempotent
/// methods (GET, HEAD, PUT, DELETE, OPTIONS) are retried to avoid duplicate side effects.
/// </summary>
public sealed class RetryHandler(RetryOptions options) : DelegatingHandler
{
    private static readonly HashSet<HttpMethod> IdempotentMethods =
        [HttpMethod.Get, HttpMethod.Head, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Options];

    public RetryHandler() : this(new RetryOptions()) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isIdempotent = IdempotentMethods.Contains(request.Method);

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex) when (isIdempotent && attempt < options.MaxRetries)
            {
                var delay = Backoff(attempt);
                options.OnRetry?.Invoke(attempt + 1, delay, $"network error: {ex.Message}");
                await options.Delay(delay, cancellationToken);
                continue;
            }

            if (attempt >= options.MaxRetries || !ShouldRetry(response.StatusCode, isIdempotent))
            {
                return response;
            }

            var wait = RetryAfter(response) ?? Backoff(attempt);
            options.OnRetry?.Invoke(attempt + 1, wait, $"HTTP {(int)response.StatusCode}");
            response.Dispose();
            await options.Delay(wait, cancellationToken);
        }
    }

    private static bool ShouldRetry(HttpStatusCode status, bool isIdempotent) => status switch
    {
        HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => true,
        HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout => isIdempotent,
        _ => false,
    };

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan wait;
        if (header?.Delta is { } delta)
        {
            wait = delta;                                   // Retry-After: 30
        }
        else if (header?.Date is { } date)
        {
            wait = date - options.Time.GetUtcNow();         // Retry-After: Wed, 11 Nov 2026 07:28:00 GMT
        }
        else
        {
            return null;
        }

        if (wait < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        return wait > options.MaxDelay ? options.MaxDelay : wait;
    }

    private TimeSpan Backoff(int attempt)
    {
        // base * 2^attempt, plus up to 20% jitter so many clients don't retry in lockstep.
        var exponential = options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt);
        var jitter = exponential * 0.2 * Random.Shared.NextDouble();
        var delay = TimeSpan.FromMilliseconds(exponential + jitter);
        return delay > options.MaxDelay ? options.MaxDelay : delay;
    }
}
