namespace TokenRefresh;

/// <summary>
/// Where tokens live between calls. In production this is usually encrypted
/// storage per tenant (database, Key Vault, Redis); the demo keeps them in memory.
/// </summary>
public interface ITokenStore
{
    ValueTask<TokenSet?> GetAsync(CancellationToken cancellationToken);
    ValueTask SaveAsync(TokenSet tokens, CancellationToken cancellationToken);
}

public sealed class InMemoryTokenStore : ITokenStore
{
    private TokenSet? _tokens;

    public ValueTask<TokenSet?> GetAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(Volatile.Read(ref _tokens));

    public ValueTask SaveAsync(TokenSet tokens, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _tokens, tokens);
        return ValueTask.CompletedTask;
    }
}
