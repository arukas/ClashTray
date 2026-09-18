using System.IO.Pipes;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal interface IServicePipeClient
{
    public Task<ServiceResponse> SendAsync(ServiceCommand command, string? payload = null, CancellationToken cancellationToken = default);
}

public sealed class ServicePipeClient : IServicePipeClient
{
    public const string PipeName = "ClashTray.Service";

    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StatusCommandTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LifecycleCommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CoreUpdateCommandTimeout = TimeSpan.FromMinutes(6);
    private const int ConnectTimeoutMilliseconds = 2000;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    public async Task<ServiceResponse> SendAsync(
        ServiceCommand command,
        string? payload = null,
        CancellationToken cancellationToken = default)
    {
        ServiceRequest request = new(Guid.NewGuid(), command, payload);
        await using NamedPipeClientStream pipe = new(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using CancellationTokenSource commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TimeSpan timeout = GetCommandTimeout(command);
        commandTimeout.CancelAfter(timeout);
        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMilliseconds, commandTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ServiceUnavailableException(
                $"服务命令 {command} 连接超时。",
                null,
                request.RequestId,
                ServiceDispatchState.NotDispatched);
        }
        catch (TimeoutException exception)
        {
            throw new ServiceUnavailableException(
                "ClashTray service is unavailable.",
                exception,
                request.RequestId,
                ServiceDispatchState.NotDispatched);
        }
        catch (IOException exception)
        {
            throw new ServiceUnavailableException(
                "ClashTray service is unavailable.",
                exception,
                request.RequestId,
                ServiceDispatchState.NotDispatched);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ServiceUnavailableException(
                "ClashTray service is unavailable.",
                exception,
                request.RequestId,
                ServiceDispatchState.NotDispatched);
        }

        await using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
        using StreamReader reader = new(pipe, leaveOpen: true);
        bool writeStarted = false;
        bool dispatched = false;
        try
        {
            writeStarted = true;
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, _options))
                .WaitAsync(commandTimeout.Token)
                .ConfigureAwait(false);
            dispatched = true;

            string? line = await reader.ReadLineAsync(commandTimeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                throw Unknown(request, "ClashTray service returned no response.");
            }

            ServiceResponse response = JsonSerializer.Deserialize<ServiceResponse>(line, _options)
                ?? throw Unknown(request, "ClashTray service returned an invalid response.");
            if (response.RequestId != request.RequestId)
            {
                throw Unknown(request, "ClashTray service returned a mismatched response.");
            }

            if (response.ProtocolVersion != ServiceProtocol.CurrentVersion
                || response.DispatchState != ServiceDispatchState.Completed)
            {
                throw Unknown(request, "ClashTray service protocol version or dispatch state is incompatible.");
            }

            return response;
        }
        catch (OperationCanceledException) when (dispatched || writeStarted)
        {
            throw Unknown(request, $"服务命令 {command} 已发送，但结果无法确认。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"服务命令 {command} 超过 {timeout.TotalSeconds:0} 秒。", null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Unknown(request, "ClashTray service returned invalid data.", exception);
        }
        catch (IOException exception)
        {
            if (writeStarted)
            {
                throw Unknown(request, "ClashTray service request result is unknown.", exception);
            }

            throw new ServiceUnavailableException(
                "ClashTray service is unavailable.",
                exception,
                request.RequestId,
                ServiceDispatchState.NotDispatched);
        }
        catch (UnauthorizedAccessException exception)
        {
            if (writeStarted)
            {
                throw Unknown(request, "ClashTray service request access was lost after dispatch.", exception);
            }

            throw new ServiceUnavailableException(
                "ClashTray service is unavailable.",
                exception,
                request.RequestId,
                ServiceDispatchState.NotDispatched);
        }
    }

    internal static TimeSpan GetCommandTimeout(ServiceCommand command) => command switch
    {
        ServiceCommand.GetStatus => StatusCommandTimeout,
        ServiceCommand.StartCore
            or ServiceCommand.StopCore
            or ServiceCommand.RestartCore => LifecycleCommandTimeout,
        ServiceCommand.InstallCore
            or ServiceCommand.RollbackCore => CoreUpdateCommandTimeout,
        _ => DefaultCommandTimeout
    };

    private static ServiceRequestUnknownException Unknown(
        ServiceRequest request,
        string message,
        Exception? innerException = null) =>
        new(
            message,
            innerException,
            request.RequestId,
            ServiceDispatchState.DispatchedAwaitingResult);
}

internal sealed class ServiceUnavailableException : IOException
{
    public ServiceUnavailableException()
        : this("ClashTray service is unavailable.", null, Guid.Empty, ServiceDispatchState.NotDispatched)
    {
    }

    public ServiceUnavailableException(string message)
        : this(message, null, Guid.Empty, ServiceDispatchState.NotDispatched)
    {
    }

    public ServiceUnavailableException(string message, Exception innerException)
        : this(message, innerException, Guid.Empty, ServiceDispatchState.NotDispatched)
    {
    }

    public ServiceUnavailableException(
        string message,
        Exception? innerException,
        Guid requestId,
        ServiceDispatchState dispatchState)
        : base(message, innerException)
    {
        RequestId = requestId;
        DispatchState = dispatchState;
    }

    public Guid RequestId { get; }

    public ServiceDispatchState DispatchState { get; }
}

internal sealed class ServiceRequestUnknownException : IOException
{
    public ServiceRequestUnknownException()
        : this("ClashTray service request result is unknown.", null, Guid.Empty, ServiceDispatchState.DispatchedAwaitingResult)
    {
    }

    public ServiceRequestUnknownException(string message)
        : this(message, null, Guid.Empty, ServiceDispatchState.DispatchedAwaitingResult)
    {
    }

    public ServiceRequestUnknownException(string message, Exception innerException)
        : this(message, innerException, Guid.Empty, ServiceDispatchState.DispatchedAwaitingResult)
    {
    }

    public ServiceRequestUnknownException(
        string message,
        Exception? innerException,
        Guid requestId,
        ServiceDispatchState dispatchState)
        : base(message, innerException)
    {
        RequestId = requestId;
        DispatchState = dispatchState;
    }

    public Guid RequestId { get; }

    public ServiceDispatchState DispatchState { get; }
}
