namespace ClashTray.Core;

/// <summary>
/// Shared safety defaults for REST and WebSocket traffic sent to a Mihomo controller.
/// Keeping these limits in one place makes the remote transport policy reviewable and testable.
/// </summary>
public static class EndpointTransportPolicy
{
    public static readonly TimeSpan DefaultRestTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultWebSocketHandshakeTimeout = TimeSpan.FromSeconds(10);

    public const int MaxJsonResponseBytes = 8 * 1024 * 1024;
    public const int MaxWebSocketMessageBytes = 256 * 1024;
    public const int MaxDisplayNameCharacters = 64;
    public const int MaxUriCharacters = 2_048;
    public const int MaxSecretCharacters = 4_096;

    // Request budgets are applied per request by MihomoApiClient through linked
    // cancellation tokens. A shorter HttpClient.Timeout would race that policy and
    // leak OperationCanceledException instead of the typed TimeoutException.
    public static HttpClient CreateControllerHttpClient() =>
        new() { Timeout = Timeout.InfiniteTimeSpan };

    internal static TimeSpan ResolveTimeout(TimeSpan? value, string parameterName, TimeSpan fallback)
    {
        TimeSpan resolved = value ?? fallback;
        if (resolved <= TimeSpan.Zero || resolved == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The endpoint timeout must be positive and finite.");
        }

        return resolved;
    }
}
