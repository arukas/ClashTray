using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal enum ControllerReadCancellationPolicy { PropagateAll, PropagateRequested }

internal sealed record ControllerResourceRead<T>(
    bool Succeeded,
    T Value,
    Exception? Failure = null,
    TimeSpan? Elapsed = null,
    string? ErrorMessage = null);

// Read-only resource policy shared by local and remote sessions. Callers own
// demand, fallback freshness, session admission and state commits.
internal static class ControllerResourceSampler
{
    public static Task<ControllerResourceRead<T>> ReadOptionalDocumentAsync<T>(
        bool include,
        Func<Task<JsonDocument>> request,
        Func<JsonDocument, T> parse,
        T fallback,
        CancellationToken cancellationToken) => include
        ? ReadDocumentAsync(request, parse, () => fallback, cancellationToken)
        : Task.FromResult(new ControllerResourceRead<T>(false, fallback));

    public static Task<ControllerResourceRead<T>> ReadDocumentAsync<T>(
        Func<Task<JsonDocument>> request,
        Func<JsonDocument, T> parse,
        Func<T> fallback,
        CancellationToken cancellationToken,
        ControllerReadCancellationPolicy cancellationPolicy = ControllerReadCancellationPolicy.PropagateAll) =>
        ReadAsync(async () =>
        {
            using JsonDocument document = await request().ConfigureAwait(false);
            return parse(document);
        }, fallback, cancellationPolicy, cancellationToken);

    public static Task<ControllerResourceRead<(IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders)>> ReadProvidersAsync(
        MihomoApiClient api,
        Func<(IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders)> fallback,
        CancellationToken cancellationToken,
        ControllerReadCancellationPolicy cancellationPolicy = ControllerReadCancellationPolicy.PropagateAll) =>
        ReadAsync(async () =>
        {
            using JsonDocument providers = await api.GetProvidersAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument ruleProviders = await api.GetRuleProvidersAsync(cancellationToken).ConfigureAwait(false);
            return (MihomoDataParser.ParseProviders(providers, "proxy"), MihomoDataParser.ParseProviders(ruleProviders, "rule"));
        }, fallback, cancellationPolicy, cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Optional resource failures become typed results preserving confirmed data; cancellation propagation is selected explicitly at the session boundary.")]
    private static async Task<ControllerResourceRead<T>> ReadAsync<T>(
        Func<Task<T>> read,
        Func<T> fallback,
        ControllerReadCancellationPolicy cancellationPolicy,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            T value = await read().ConfigureAwait(false);
            return new(true, value, Elapsed: Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            || cancellationPolicy == ControllerReadCancellationPolicy.PropagateAll)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new(false, fallback(), exception, Stopwatch.GetElapsedTime(started), ErrorSanitizer.Sanitize(exception));
        }
    }
}
