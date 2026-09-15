using System.Net.WebSockets;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class EndpointSession : IAsyncDisposable
{
    private readonly EndpointTransport _transport;
    private int _disposed;

    public EndpointSession(
        EndpointTransport transport,
        EndpointCapability capabilities,
        long generation,
        long selectionRevision,
        EndpointHandshakeResult? handshake = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(selectionRevision);

        _transport = transport;
        Endpoint = transport.Endpoint;
        Api = new MihomoApiClient(
            transport.HttpClient,
            transport.BaseUri,
            string.Empty,
            restTimeout: transport.RestTimeout,
            writeTimeout: transport.WriteTimeout,
            webSocketHandshakeTimeout: transport.WebSocketHandshakeTimeout,
            webSocketFactory: transport.CreateWebSocket,
            webSocketUriBuilder: transport.BuildWebSocketUri);
        Capabilities = capabilities;
        Generation = generation;
        SelectionRevision = selectionRevision;
        Handshake = handshake ?? new EndpointHandshakeResult(
            EndpointSessionState.Connected,
            Version: null,
            capabilities,
            ErrorMessage: null);
        ConnectedAt = DateTimeOffset.UtcNow;
    }

    public EndpointDescriptor Endpoint { get; }

    public MihomoApiClient Api { get; }

    public EndpointCapability Capabilities { get; }

    public long Generation { get; }

    public long SelectionRevision { get; }

    public EndpointHandshakeResult Handshake { get; }

    public DateTimeOffset ConnectedAt { get; }

    public ClientWebSocket CreateWebSocket()
    {
        ThrowIfDisposed();
        return _transport.CreateWebSocket();
    }

    public Uri BuildWebSocketUri(string path)
    {
        ThrowIfDisposed();
        return _transport.BuildWebSocketUri(path);
    }

    public async Task<ClientWebSocket> ConnectWebSocketAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await Api.ConnectWebSocketAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _transport.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

public interface IEndpointSessionConnector
{
    public Task<EndpointSession> ConnectAsync(
        EndpointDescriptor endpoint,
        long generation,
        long selectionRevision,
        CancellationToken cancellationToken);
}

public sealed class EndpointSessionConnectException : IOException
{
    public EndpointSessionConnectException()
        : this(EndpointSessionState.Failed, false, "端点连接失败。")
    {
    }

    public EndpointSessionConnectException(string message)
        : this(EndpointSessionState.Failed, false, message)
    {
    }

    public EndpointSessionConnectException(string message, Exception innerException)
        : this(EndpointSessionState.Failed, false, message, innerException)
    {
    }

    public EndpointSessionConnectException(
        EndpointSessionState failureState,
        bool isTransient,
        string message,
        Exception? innerException = null)
        : base(ErrorSanitizer.Sanitize(message), innerException)
    {
        if (failureState is not (
            EndpointSessionState.AuthenticationFailed
            or EndpointSessionState.CertificateFailed
            or EndpointSessionState.Incompatible
            or EndpointSessionState.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(failureState));
        }

        FailureState = failureState;
        IsTransient = isTransient;
    }

    public EndpointSessionState FailureState { get; }

    public bool IsTransient { get; }
}
