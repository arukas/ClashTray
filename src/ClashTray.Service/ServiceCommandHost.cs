using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.Service;

internal sealed class ServiceCommandHost : IAsyncDisposable
{
    private readonly string _userSid;
    private readonly CancellationTokenSource _cts = new();
    private readonly ServiceRuntimeController _controller = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private Task? _serverTask;

    public ServiceCommandHost(string userSid)
    {
        _userSid = userSid;
    }

    public void Start() => _serverTask = Task.Run(RunAsync);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
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
            await using var pipe = CreatePipe();
            await pipe.WaitForConnectionAsync(_cts.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            var line = await reader.ReadLineAsync(_cts.Token);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ServiceResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<ServiceRequest>(line, _jsonOptions)
                    ?? throw new InvalidDataException("Invalid service request.");
                response = await _controller.HandleAsync(request, _cts.Token);
            }
            catch (Exception exception)
            {
                response = new ServiceResponse(Guid.Empty, false, TunState.Failed, Error: exception.Message, Core: _controller.CoreState);
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(response, _jsonOptions));
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        var user = new SecurityIdentifier(_userSid);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        if (user.IsWellKnown(WellKnownSidType.LocalSystemSid))
        {
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
        }
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
