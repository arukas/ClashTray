namespace ClashTray.Core;

/// <summary>Retains the last readiness observation when a bounded wait is interrupted.</summary>
internal sealed class ListenerReadinessDeadline : IDisposable
{
    private readonly CancellationTokenSource _timeout;
    private readonly CancellationToken _callerToken;
    private readonly TimeSpan _budget;
    private string? _listenerDetail;
    private string? _errorDetail;

    public ListenerReadinessDeadline(TimeSpan budget, CancellationToken callerToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        _budget = budget;
        _callerToken = callerToken;
        _timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        _timeout.CancelAfter(budget);
    }

    public CancellationToken Token => _timeout.Token;

    public string Diagnostic => string.Join(" ", new[] { _listenerDetail, _errorDetail }
        .Where(detail => !string.IsNullOrWhiteSpace(detail)));

    public void Observe(LocalPortBinding listener, ListenerOwnerObservation observation)
    {
        _listenerDetail = ListenerReadinessEvaluator.Describe(listener, observation);
        _errorDetail = null;
    }

    public void Observe(ListenerReadinessResult result)
    {
        _listenerDetail = result.Detail;
        _errorDetail = null;
    }

    public void RecordError(Exception exception) => _errorDetail = ErrorSanitizer.Sanitize(exception);

    public TimeoutException CreateTimeoutException(Exception? innerException = null) => new(
        $"Mihomo 在 {_budget.TotalSeconds:0.###} 秒内未完成监听确认。{Diagnostic}", innerException);

    public ListenerReadinessCancelledException CreateCancellationException(OperationCanceledException exception) =>
        new(Diagnostic, exception, _callerToken);

    public void Dispose() => _timeout.Dispose();
}

internal sealed class ListenerReadinessCancelledException : OperationCanceledException
{
    public ListenerReadinessCancelledException()
    {
    }

    public ListenerReadinessCancelledException(string message) : base(message)
    {
    }

    public ListenerReadinessCancelledException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public ListenerReadinessCancelledException(string diagnostic, OperationCanceledException innerException,
        CancellationToken cancellationToken)
        : base($"Mihomo 监听确认已中断。{diagnostic}", innerException, cancellationToken)
    {
        Diagnostic = diagnostic;
    }

    public string Diagnostic { get; } = string.Empty;
}
