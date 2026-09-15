using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

public enum MihomoStreamFailureKind
{
    EmptyResponse,
    DisconnectedBeforeRecord,
    InvalidJson,
    RecordTooLarge
}

public sealed class MihomoStreamException : IOException
{
    public MihomoStreamException()
        : this(string.Empty, MihomoStreamFailureKind.InvalidJson, string.Empty)
    {
    }

    public MihomoStreamException(string message)
        : this(string.Empty, MihomoStreamFailureKind.InvalidJson, message)
    {
    }

    public MihomoStreamException(string message, Exception innerException)
        : this(string.Empty, MihomoStreamFailureKind.InvalidJson, message, innerException)
    {
    }


    public MihomoStreamException(
        string path,
        MihomoStreamFailureKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Path = path;
        Kind = kind;
    }

    public string Path { get; }

    public MihomoStreamFailureKind Kind { get; }
}

public sealed class MihomoApiClient
{
    public static readonly TimeSpan DefaultRestTimeout = EndpointTransportPolicy.DefaultRestTimeout;
    public static readonly TimeSpan DefaultWriteTimeout = EndpointTransportPolicy.DefaultWriteTimeout;
    public static readonly TimeSpan DefaultWebSocketHandshakeTimeout = EndpointTransportPolicy.DefaultWebSocketHandshakeTimeout;
    public const int DefaultMaxJsonResponseBytes = EndpointTransportPolicy.MaxJsonResponseBytes;
    private const int StreamingReadBufferBytes = 16 * 1024;
    public const int DefaultMaxStreamingRecordBytes = 256 * 1024;
    public static readonly TimeSpan DefaultStreamingFirstRecordTimeout = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _controllerUri;
    private readonly string _secret;
    private readonly Func<ClientWebSocket>? _webSocketFactory;
    private readonly Func<string, Uri>? _webSocketUriBuilder;
    private readonly TimeSpan _restTimeout;
    private readonly TimeSpan _writeTimeout;
    private readonly TimeSpan _webSocketHandshakeTimeout;
    private readonly int _maxJsonResponseBytes;
    private readonly TimeSpan _streamingFirstRecordTimeout;
    private readonly int _maxStreamingRecordBytes;

    public MihomoApiClient(
        HttpClient httpClient,
        Uri controllerUri,
        string secret,
        TimeSpan? streamingFirstRecordTimeout = null,
        int maxStreamingRecordBytes = DefaultMaxStreamingRecordBytes,
        Func<ClientWebSocket>? webSocketFactory = null,
        Func<string, Uri>? webSocketUriBuilder = null,
        TimeSpan? restTimeout = null,
        TimeSpan? writeTimeout = null,
        TimeSpan? webSocketHandshakeTimeout = null,
        int maxJsonResponseBytes = DefaultMaxJsonResponseBytes)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(controllerUri);
        ArgumentNullException.ThrowIfNull(secret);
        if (streamingFirstRecordTimeout is not null && streamingFirstRecordTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(streamingFirstRecordTimeout));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStreamingRecordBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxJsonResponseBytes);

        _httpClient = httpClient;
        _controllerUri = controllerUri;
        _secret = secret;
        _webSocketFactory = webSocketFactory;
        _webSocketUriBuilder = webSocketUriBuilder;
        _restTimeout = EndpointTransportPolicy.ResolveTimeout(
            restTimeout,
            nameof(restTimeout),
            DefaultRestTimeout);
        _writeTimeout = EndpointTransportPolicy.ResolveTimeout(
            writeTimeout,
            nameof(writeTimeout),
            DefaultWriteTimeout);
        _webSocketHandshakeTimeout = EndpointTransportPolicy.ResolveTimeout(
            webSocketHandshakeTimeout,
            nameof(webSocketHandshakeTimeout),
            DefaultWebSocketHandshakeTimeout);
        _maxJsonResponseBytes = maxJsonResponseBytes;
        _streamingFirstRecordTimeout = streamingFirstRecordTimeout ?? DefaultStreamingFirstRecordTimeout;
        _maxStreamingRecordBytes = maxStreamingRecordBytes;
    }

    public async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, path);
        return await SendJsonAsync(
            request,
            _restTimeout,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonDocument> PutAsync(string path, object payload, CancellationToken cancellationToken = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Put, path);
        using (request)
        {
            request.Content = JsonContent.Create(payload, options: JsonOptions);
            return await SendJsonAsync(
                request,
                _writeTimeout,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<JsonDocument> PostAsync(string path, object? payload = null, CancellationToken cancellationToken = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, path);
        using (request)
        {
            if (payload is not null)
            {
                request.Content = JsonContent.Create(payload, options: JsonOptions);
            }

            return await SendJsonAsync(
                request,
                _writeTimeout,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<JsonDocument> PatchAsync(string path, object payload, CancellationToken cancellationToken = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Patch, path);
        using (request)
        {
            request.Content = JsonContent.Create(payload, options: JsonOptions);
            return await SendJsonAsync(
                request,
                _writeTimeout,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Delete, path);
        await SendAsync(
            request,
            _writeTimeout,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<JsonDocument> GetVersionAsync(CancellationToken cancellationToken = default) =>
        GetAsync("/version", cancellationToken);

    public Task<JsonDocument> GetConfigurationAsync(bool force, CancellationToken cancellationToken = default) =>
        GetAsync(force ? "/configs?force=true" : "/configs", cancellationToken);

    public Task<JsonDocument> GetProxiesAsync(CancellationToken cancellationToken = default) =>
        GetAsync("/proxies", cancellationToken);

    public Task<JsonDocument> GetConnectionsAsync(CancellationToken cancellationToken = default) =>
        GetAsync("/connections", cancellationToken);

    public Task<JsonDocument> GetRulesAsync(CancellationToken cancellationToken = default) =>
        GetAsync("/rules", cancellationToken);

    public Task<JsonDocument> GetTrafficAsync(CancellationToken cancellationToken = default) =>
        GetStreamingSnapshotAsync("/traffic", cancellationToken);

    public Task<JsonDocument> GetMemoryAsync(CancellationToken cancellationToken = default) =>
        GetStreamingSnapshotAsync("/memory", cancellationToken);

    public Task<JsonDocument> GetLogsAsync(string level = "info", CancellationToken cancellationToken = default) =>
        GetAsync($"/logs?level={Uri.EscapeDataString(level)}&format=structured", cancellationToken);

    public Task<JsonDocument> GetProvidersAsync(CancellationToken cancellationToken = default) =>
        GetAsync("/providers/proxies", cancellationToken);

    public Task<JsonDocument> GetRuleProvidersAsync(CancellationToken cancellationToken = default) =>
        GetAsync("/providers/rules", cancellationToken);

    public Task<JsonDocument> ClearDnsCacheAsync(CancellationToken cancellationToken = default) =>
        PostAsync("/cache/dns/flush", cancellationToken: cancellationToken);

    public Task<JsonDocument> ClearFakeIpCacheAsync(CancellationToken cancellationToken = default) =>
        PostAsync("/cache/fakeip/flush", cancellationToken: cancellationToken);

    public Task<JsonDocument> UpdateGeoAsync(CancellationToken cancellationToken = default) =>
        PostAsync("/configs/geo", cancellationToken: cancellationToken);

    public Task<JsonDocument> SetTunAsync(bool enabled, CancellationToken cancellationToken = default) =>
        PatchAsync("/configs", new { tun = new { enable = enabled } }, cancellationToken);

    public Task<JsonDocument> SetNetworkSettingsAsync(
        bool? allowLan,
        bool? ipv6,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, bool> payload = new Dictionary<string, bool>();
        if (allowLan is bool allowLanValue)
        {
            payload["allow-lan"] = allowLanValue;
        }

        if (ipv6 is bool ipv6Value)
        {
            payload["ipv6"] = ipv6Value;
        }

        if (payload.Count == 0)
        {
            throw new ArgumentException("至少需要提供一个 Mihomo 网络设置。", nameof(allowLan));
        }

        return PatchAsync("/configs", payload, cancellationToken);
    }

    public Task<JsonDocument> SelectProxyAsync(string group, string proxy, CancellationToken cancellationToken = default) =>
        PutAsync($"/proxies/{Uri.EscapeDataString(group)}", new { name = proxy }, cancellationToken);

    public Task<JsonDocument> TestDelayAsync(string proxy, Uri url, int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(url);
        return GetAsync($"/proxies/{Uri.EscapeDataString(proxy)}/delay?url={Uri.EscapeDataString(url.ToString())}&timeout={timeoutMilliseconds}", cancellationToken);
    }

    public Task<JsonDocument> TestGroupDelayAsync(string group, Uri url, int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(url);
        return GetAsync($"/group/{Uri.EscapeDataString(group)}/delay?url={Uri.EscapeDataString(url.ToString())}&timeout={timeoutMilliseconds}", cancellationToken);
    }

    public Task<JsonDocument> SetModeAsync(ProxyMode mode, CancellationToken cancellationToken = default) =>
        PatchAsync("/configs", new { mode = mode.ToString().ToLowerInvariant() }, cancellationToken);

    public Task RefreshProviderAsync(string providerName, CancellationToken cancellationToken = default) =>
        PutAsync($"/providers/proxies/{Uri.EscapeDataString(providerName)}", new { }, cancellationToken);

    public Task RefreshRuleProviderAsync(string providerName, CancellationToken cancellationToken = default) =>
        PutAsync($"/providers/rules/{Uri.EscapeDataString(providerName)}", new { }, cancellationToken);

    public Task CloseConnectionAsync(string id, CancellationToken cancellationToken = default) =>
        DeleteAsync($"/connections/{Uri.EscapeDataString(id)}", cancellationToken);

    public Task CloseAllConnectionsAsync(CancellationToken cancellationToken = default) =>
        DeleteAsync("/connections", cancellationToken);

    public ClientWebSocket CreateWebSocket()
    {
        if (_webSocketFactory is not null)
        {
            return _webSocketFactory();
        }

        ClientWebSocket socket = new ClientWebSocket();
        if (_secret.Length > 0)
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {_secret}");
        }

        return socket;
    }

    public async Task<ClientWebSocket> ConnectWebSocketAsync(string path, CancellationToken cancellationToken = default)
    {
        ClientWebSocket? socket = null;
        using CancellationTokenSource timeout = CreateTimeoutSource(
            _webSocketHandshakeTimeout,
            cancellationToken);
        try
        {
            socket = CreateWebSocket();
            await socket.ConnectAsync(BuildWebSocketUri(path), timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Mihomo WebSocket 握手超时。", exception);
        }
        catch
        {
            socket?.Dispose();
            throw;
        }
    }

    public Uri BuildWebSocketUri(string path)
    {
        Uri requestUri = BuildControllerUri(path);
        if (_webSocketUriBuilder is not null)
        {
            return _webSocketUriBuilder(path);
        }

        UriBuilder builder = new UriBuilder(requestUri)
        {
            Scheme = _controllerUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
        };
        return builder.Uri;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        HttpRequestMessage request = new HttpRequestMessage(method, BuildControllerUri(path));
        if (_secret.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secret);
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private Uri BuildControllerUri(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Uri.TryCreate(path, UriKind.Absolute, out _)
            || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Mihomo controller paths must remain relative to the configured controller.",
                nameof(path));
        }

        return new Uri(_controllerUri, path.TrimStart('/'));
    }

    private static Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return Task.CompletedTask;
        }

        throw new HttpRequestException(
            $"Mihomo controller returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "Unknown status"}).",
            inner: null,
            statusCode: response.StatusCode);
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource = CreateTimeoutSource(timeout, cancellationToken);
        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    completionOption,
                    timeoutSource.Token)
                .ConfigureAwait(false);
            await EnsureSuccessAsync(response, timeoutSource.Token).ConfigureAwait(false);
            return await ReadJsonAsync(response.Content, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException("Mihomo Controller 请求超时。", exception);
        }
    }

    private async Task SendAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource = CreateTimeoutSource(timeout, cancellationToken);
        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    completionOption,
                    timeoutSource.Token)
                .ConfigureAwait(false);
            await EnsureSuccessAsync(response, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException("Mihomo Controller 请求超时。", exception);
        }
    }

    private static CancellationTokenSource CreateTimeoutSource(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private async Task<JsonDocument> ReadJsonAsync(HttpContent content, CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadBytesAsync(content, _maxJsonResponseBytes, cancellationToken).ConfigureAwait(false);
        return bytes.Length == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(bytes);
    }

    private static async Task<byte[]> ReadBytesAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidDataException("Mihomo controller response exceeded the maximum size.");
        }

        await using Stream input = await content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream buffer = new MemoryStream();
        byte[] chunk = new byte[64 * 1024];
        while (true)
        {
            int count = await input.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (count == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length > maxBytes - count)
            {
                throw new InvalidDataException("Mihomo controller response exceeded the maximum size.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
        }
    }

    private async Task<JsonDocument> GetStreamingSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, path);
        using CancellationTokenSource operationTimeout = CreateTimeoutSource(_restTimeout, cancellationToken);
        using CancellationTokenSource firstRecordTimeout = CancellationTokenSource.CreateLinkedTokenSource(operationTimeout.Token);
        firstRecordTimeout.CancelAfter(_streamingFirstRecordTimeout);

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                firstRecordTimeout.Token);
            await EnsureSuccessAsync(response, firstRecordTimeout.Token).ConfigureAwait(false);
            return await ReadFirstJsonLineAsync(response.Content, path, firstRecordTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested
                && (firstRecordTimeout.IsCancellationRequested || operationTimeout.IsCancellationRequested))
        {
            throw new TimeoutException(
                $"Mihomo {path} 首条指标记录读取超时。",
                exception);
        }
    }

    private async Task<JsonDocument> ReadFirstJsonLineAsync(
        HttpContent content,
        string path,
        CancellationToken cancellationToken)
    {
        await using Stream input = await content.ReadAsStreamAsync(cancellationToken);
        byte[] readBuffer = new byte[StreamingReadBufferBytes];
        using MemoryStream line = new MemoryStream(Math.Min(_maxStreamingRecordBytes, StreamingReadBufferBytes));
        bool receivedAnyBytes = false;

        while (true)
        {
            int count = await input.ReadAsync(readBuffer.AsMemory(), cancellationToken);
            if (count == 0)
            {
                throw new MihomoStreamException(
                    path,
                    line.Length == 0 && !receivedAnyBytes
                        ? MihomoStreamFailureKind.EmptyResponse
                        : MihomoStreamFailureKind.DisconnectedBeforeRecord,
                    line.Length == 0 && !receivedAnyBytes
                        ? $"Mihomo {path} 返回空响应。"
                        : $"Mihomo {path} 在首条完整 JSON 行之前断开连接。");
            }

            receivedAnyBytes = true;
            for (int index = 0; index < count; index++)
            {
                byte value = readBuffer[index];
                if (value == (byte)'\n')
                {
                    JsonDocument? document = TryParseJsonLine(line, path);
                    line.SetLength(0);
                    if (document is not null)
                    {
                        return document;
                    }

                    continue;
                }

                if (line.Length >= _maxStreamingRecordBytes)
                {
                    throw new MihomoStreamException(
                        path,
                        MihomoStreamFailureKind.RecordTooLarge,
                        $"Mihomo {path} 单条指标记录超过 {_maxStreamingRecordBytes} 字节限制。");
                }

                line.WriteByte(value);
            }
        }
    }

    private static JsonDocument? TryParseJsonLine(MemoryStream line, string path)
    {
        byte[] bytes = line.ToArray();
        int length = bytes.Length;
        if (length > 0 && bytes[length - 1] == (byte)'\r')
        {
            length--;
        }

        if (length == 0 || IsWhitespace(bytes.AsSpan(0, length)))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(bytes.AsMemory(0, length));
        }
        catch (JsonException exception)
        {
            throw new MihomoStreamException(
                path,
                MihomoStreamFailureKind.InvalidJson,
                $"Mihomo {path} 首条指标记录不是有效 JSON。",
                exception);
        }
    }

    private static bool IsWhitespace(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
            {
                return false;
            }
        }

        return true;
    }
}
