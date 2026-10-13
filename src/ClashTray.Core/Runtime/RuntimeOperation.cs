namespace ClashTray.Core;

/// <summary>
/// Identifies the point at which an operation crossed an external side-effect
/// boundary. A caller can stop waiting after dispatch without reinterpreting
/// the operation as if it had never been sent.
/// </summary>
public enum OperationDispatchState
{
    NotDispatched,
    DispatchedAwaitingResult,
    Completed
}

public enum OperationOutcome
{
    Applied,
    Superseded,
    Busy,
    CanceledBeforeDispatch,
    Failed,
    RolledBack,
    UnknownOutcome
}

public sealed record RuntimeOperationContext(
    Guid OperationId,
    string OperationKind,
    string? Target,
    long AcceptedSnapshotRevision,
    long CoreLifecycleEpoch,
    long ProcessGeneration,
    long ControllerGeneration,
    DateTimeOffset Deadline)
{
    public static RuntimeOperationContext Create(
        string operationKind,
        string? target,
        long acceptedSnapshotRevision,
        long coreLifecycleEpoch,
        long processGeneration,
        long controllerGeneration,
        TimeSpan budget) =>
        new(
            Guid.NewGuid(),
            string.IsNullOrWhiteSpace(operationKind) ? "operation" : operationKind,
            target,
            acceptedSnapshotRevision,
            coreLifecycleEpoch,
            processGeneration,
            controllerGeneration,
            DateTimeOffset.UtcNow.Add(budget));

    public bool IsExpired(DateTimeOffset? now = null) =>
        (now ?? DateTimeOffset.UtcNow) >= Deadline;
}

public sealed record OperationResult<T>(
    OperationOutcome Outcome,
    T? Value = default,
    Exception? Error = null);

public sealed class OperationSupersededException : OperationCanceledException
{
    public OperationSupersededException()
        : this("操作")
    {
    }

    public OperationSupersededException(string operationName)
        : base($"{operationName} 操作已被更新的目标覆盖。")
    {
        OperationName = string.IsNullOrWhiteSpace(operationName) ? "操作" : operationName;
        Outcome = OperationOutcome.Superseded;
    }

    public OperationSupersededException(string message, Exception innerException)
        : base(message, innerException)
    {
        OperationName = "操作";
        Outcome = OperationOutcome.Superseded;
    }

    public string OperationName { get; }

    public OperationOutcome Outcome { get; }
}

public sealed class RuntimeQuiescingException : InvalidOperationException
{
    public RuntimeQuiescingException(string message)
        : base(message)
    {
        Outcome = OperationOutcome.Busy;
    }

    public RuntimeQuiescingException(string message, Exception innerException)
        : base(message, innerException)
    {
        Outcome = OperationOutcome.Busy;
    }

    public RuntimeQuiescingException()
        : base("ClashTray 正在退出，不再接受新的操作。")
    {
        Outcome = OperationOutcome.Busy;
    }

    public OperationOutcome Outcome { get; }
}
