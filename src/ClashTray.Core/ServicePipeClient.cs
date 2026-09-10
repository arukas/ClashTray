using System.IO.Pipes;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal interface IServicePipeClient
{
    Task<ServiceResponse> SendAsync(ServiceCommand command, string? payload = null, CancellationToken cancellationToken = default);
}

public sealed class ServicePipeClient : IServicePipeClient
{
    public const string PipeName = "ClashTray.Service";

    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    public async Task<ServiceResponse> SendAsync(ServiceCommand command, string? payload = null, CancellationToken cancellationToken = default)
    {
        var request = new ServiceRequest(Guid.NewGuid(), command, payload);
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(2000, cancellationToken);
        }
        catch (IOException exception)
        {
            throw new ServiceUnavailableException("ClashTray service is unavailable.", exception);
        }

        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, _options));
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidDataException("ClashTray service returned no response.");
            }

            var response = JsonSerializer.Deserialize<ServiceResponse>(line, _options)
                ?? throw new InvalidDataException("ClashTray service returned an invalid response.");
            if (response.RequestId != request.RequestId)
            {
                throw new InvalidDataException("ClashTray service returned a mismatched response.");
            }

            return response;
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
}

internal sealed class ServiceUnavailableException : IOException
{
    public ServiceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class ServiceRequestUnknownException : IOException
{
    public ServiceRequestUnknownException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}


