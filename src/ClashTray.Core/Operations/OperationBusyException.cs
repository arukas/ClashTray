namespace ClashTray.Core;

/// <summary>
/// Indicates that a non-queueable operation was requested while a conflicting
/// operation was already admitted.
/// </summary>
public sealed class OperationBusyException : InvalidOperationException
{
    public OperationBusyException()
        : this("操作")
    {
    }

    public OperationBusyException(string operationName)
        : base($"{operationName} 操作正在进行，请稍后重试。")
    {
        OperationName = string.IsNullOrWhiteSpace(operationName) ? "操作" : operationName;
        Outcome = OperationOutcome.Busy;
    }

    public OperationBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
        OperationName = "操作";
        Outcome = OperationOutcome.Busy;
    }

    public string OperationName { get; }

    public OperationOutcome Outcome { get; }
}
