using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.Service;

internal sealed class ServiceCommandHost : IAsyncDisposable
{
    private const int MaxRequestCharacters = 64 * 1024;
    private readonly string _userSid;
    private readonly CancellationTokenSource _cts = new();
    private readonly ServiceRuntimeController _controller;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private Task? _serverTask;

    public ServiceCommandHost(string userSid)
    {
        _userSid = userSid;
        _controller = new ServiceRuntimeController(managedUserSid: userSid);
    }

    public void Start() => _serverTask = Task.Run(RunAsync);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_serverTask is not null)
        {
            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _controller.DisposeAsync();
        _cts.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            await using NamedPipeServerStream pipe = CreatePipe();
            try
            {
                await pipe.WaitForConnectionAsync(_cts.Token);
                using StreamReader reader = new StreamReader(pipe, leaveOpen: true);
                await using StreamWriter writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                string? line;
                try
                {
                    line = await ReadLineLimitedAsync(reader, _cts.Token);
                }
                catch (InvalidDataException)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(
                        new ServiceResponse(Guid.Empty, false, TunState.Failed, Error: "服务请求超过大小限制。", Core: _controller.CoreState)));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ServiceResponse response;
                try
                {
                    ServiceRequest request = JsonSerializer.Deserialize<ServiceRequest>(line, _jsonOptions)
                        ?? throw new InvalidDataException("Invalid service request.");
                    response = await _controller.HandleAsync(request, _cts.Token);
                }
                catch (Exception exception)
                {
                    response = new ServiceResponse(Guid.Empty, false, TunState.Failed, Error: exception.Message, Core: _controller.CoreState);
                }

                await writer.WriteLineAsync(JsonSerializer.Serialize(response, _jsonOptions));
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (!_cts.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task<string?> ReadLineLimitedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        StringBuilder builder = new System.Text.StringBuilder();
        char[] buffer = new char[1024];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
            {
                return builder.Length == 0 ? null : builder.ToString().TrimEnd('\r');
            }

            for (int index = 0; index < count; index++)
            {
                char character = buffer[index];
                if (character == '\n')
                {
                    return builder.ToString().TrimEnd('\r');
                }

                if (builder.Length >= MaxRequestCharacters)
                {
                    throw new InvalidDataException("Service request exceeded the maximum message size.");
                }

                builder.Append(character);
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        PipeSecurity security = new PipeSecurity();
        SecurityIdentifier user = new SecurityIdentifier(_userSid);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            ServicePipeClient.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security);
    }
}
