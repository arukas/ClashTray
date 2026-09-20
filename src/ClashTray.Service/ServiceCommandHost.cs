using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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
    private const int MaxServerInstances = 32;
    private static readonly TimeSpan RequestIdleTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ResponseWriteTimeout = TimeSpan.FromSeconds(5);
    private readonly string _userSid;
    private readonly CancellationTokenSource _cts = new();
    private readonly ServiceRuntimeController _controller;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<int, Task> _clientTasks = new();
    private readonly SemaphoreSlim _clientSlots = new(MaxServerInstances, MaxServerInstances);
    private Task? _serverTask;
    private int _nextClientId;

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
        _clientSlots.Dispose();
        _cts.Dispose();
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Connected pipes transfer ownership to a tracked client task; every untransferred pipe is disposed in finally.")]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The shutdown join only observes client tasks while cancellation is requested; individual pipe failures are already handled by HandleClientAsync.")]
    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _clientSlots.WaitAsync(_cts.Token);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }

            NamedPipeServerStream? pipe = null;
            bool slotTransferred = false;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(_cts.Token);
                TrackClient(pipe);
                pipe = null;
                slotTransferred = true;
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (TimeoutException) when (!_cts.IsCancellationRequested)
            {
            }
            catch (IOException) when (!_cts.IsCancellationRequested)
            {
            }
            finally
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync();
                }

                if (!slotTransferred)
                {
                    _clientSlots.Release();
                }
            }
        }

        while (!_clientTasks.IsEmpty)
        {
            Task[] clients = _clientTasks.Values.ToArray();
            try
            {
                await Task.WhenAll(clients);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                // Client shutdown is driven by the host cancellation token.
                // Individual pipe failures are handled by HandleClientAsync.
            }
        }
    }

    private void TrackClient(NamedPipeServerStream pipe)
    {
        int clientId = Interlocked.Increment(ref _nextClientId);
        Task clientTask = HandleClientAsync(pipe);
        _clientTasks[clientId] = clientTask;
        _ = clientTask.ContinueWith(
            completedTask =>
            {
                _ = completedTask.Exception;
                _clientTasks.TryRemove(clientId, out _);
                _clientSlots.Release();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The named-pipe command boundary converts any handler failure into a bounded ServiceResponse so a single bad request cannot crash the service.")]
    private async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using StreamReader reader = new StreamReader(pipe, leaveOpen: true);
                await using StreamWriter writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                string? line;
                try
                {
                    using CancellationTokenSource requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    requestTimeout.CancelAfter(RequestIdleTimeout);
                    line = await ReadLineLimitedAsync(reader, requestTimeout.Token);
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (InvalidDataException)
                {
                    await WriteResponseAsync(
                        writer,
                        new ServiceResponse(
                            Guid.Empty,
                            false,
                            TunState.Failed,
                            Error: "服务请求超过大小限制。",
                            Core: _controller.CoreState,
                            ProtocolVersion: ServiceProtocol.CurrentVersion),
                        _cts.Token);
                    return;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                ServiceRequest? request = null;
                ServiceResponse response;
                try
                {
                    request = JsonSerializer.Deserialize<ServiceRequest>(line, _jsonOptions)
                        ?? throw new InvalidDataException("Invalid service request.");
                    response = await _controller.HandleAsync(request, _cts.Token);
                }
                catch (Exception exception)
                {
                    response = new ServiceResponse(
                        request?.RequestId ?? Guid.Empty,
                        false,
                        TunState.Failed,
                        Error: ErrorSanitizer.Sanitize(exception),
                        Core: _controller.CoreState,
                        ErrorCode: exception is OperationBusyException
                            ? ServiceErrorCode.OperationBusy
                            : ServiceErrorCode.None,
                        ProtocolVersion: ServiceProtocol.CurrentVersion);
                }

                await WriteResponseAsync(writer, response, _cts.Token);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
            catch (TimeoutException) when (!_cts.IsCancellationRequested)
            {
            }
            catch (IOException) when (!_cts.IsCancellationRequested)
            {
            }
        }
    }

    private async Task WriteResponseAsync(
        StreamWriter writer,
        ServiceResponse response,
        CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, _jsonOptions))
            .WaitAsync(ResponseWriteTimeout, cancellationToken);
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
            MaxServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security);
    }
}
