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

    public async Task<ServiceResponse> SendAsync(ServiceCommand command, string? payload = null, CancellationToken cancellationToken = default)
    {
        ServiceRequest request = new ServiceRequest(Guid.NewGuid(), command, payload);
        await using NamedPipeClientStream pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using CancellationTokenSource commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TimeSpan timeout = GetCommandTimeout(command);
        commandTimeout.CancelAfter(timeout);
        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMilliseconds, commandTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"服务命令 {command} 超过 {timeout.TotalSeconds:0} 秒。");
        }
        catch (TimeoutException exception)
        {
            throw new ServiceUnavailableException("ClashTray service is unavailable.", exception);
        }
        catch (IOException exception)
        {
            throw new ServiceUnavailableException("ClashTray service is unavailable.", exception);
        }

        await using StreamWriter writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using StreamReader reader = new StreamReader(pipe, leaveOpen: true);
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, _options)).WaitAsync(commandTimeout.Token);
            string? line = await reader.ReadLineAsync(commandTimeout.Token);
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidDataException("ClashTray service returned no response.");
            }

            ServiceResponse response = JsonSerializer.Deserialize<ServiceResponse>(line, _options)
                ?? throw new InvalidDataException("ClashTray service returned an invalid response.");
            if (response.RequestId != request.RequestId)
            {
                throw new InvalidDataException("ClashTray service returned a mismatched response.");
            }

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"服务命令 {command} 超过 {timeout.TotalSeconds:0} 秒。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ServiceRequestUnknownException("ClashTray service returned invalid data.", exception);
        }
        catch (IOException exception)
        {
            throw new ServiceRequestUnknownException("ClashTray service request result is unknown.", exception);
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
}

internal sealed class ServiceUnavailableException : IOException
{
    public ServiceUnavailableException()
    {
    }

    public ServiceUnavailableException(string message)
        : base(message)
    {
    }


    public ServiceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class ServiceRequestUnknownException : IOException
{
    public ServiceRequestUnknownException()
    {
    }

    public ServiceRequestUnknownException(string message)
        : base(message)
    {
    }


    public ServiceRequestUnknownException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}


