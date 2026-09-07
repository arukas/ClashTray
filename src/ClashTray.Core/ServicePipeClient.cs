using System.IO.Pipes;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class ServicePipeClient
{
    public const string PipeName = "ClashTray.Service";

    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    public async Task<ServiceResponse> SendAsync(ServiceCommand command, string? payload = null, CancellationToken cancellationToken = default)
    {
        var request = new ServiceRequest(Guid.NewGuid(), command, payload);
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000, cancellationToken);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, _options));
        var line = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new IOException("ClashTray service returned no response.");
        }

        var response = JsonSerializer.Deserialize<ServiceResponse>(line, _options)
            ?? throw new InvalidDataException("ClashTray service returned an invalid response.");
        if (response.RequestId != request.RequestId)
        {
            throw new InvalidDataException("ClashTray service returned a mismatched response.");
        }

        return response;
    }
}
