using ClashTray.Contracts;

namespace ClashTray.Core;

/// <summary>
/// Preserves the service's stable error classification while carrying its
/// sanitized diagnostic text across the runtime boundary.
/// </summary>
public sealed class ServiceCommandException : InvalidOperationException
{
    public ServiceCommandException()
        : this(ServiceErrorCode.None, "The service command failed.")
    {
    }

    public ServiceCommandException(string message)
        : this(ServiceErrorCode.None, message)
    {
    }

    public ServiceCommandException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ServiceCommandException(ServiceErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public ServiceErrorCode ErrorCode { get; }
}
