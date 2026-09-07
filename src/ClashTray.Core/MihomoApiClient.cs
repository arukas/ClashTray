using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class MihomoApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _controllerUri;
    private readonly string _secret;

    public MihomoApiClient(HttpClient httpClient, Uri controllerUri, string secret)
    {
        _httpClient = httpClient;
        _controllerUri = controllerUri;
        _secret = secret;
    }

    public async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    public async Task<JsonDocument> PutAsync(string path, object payload, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Put, path);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync(response.Content, cancellationToken);
    }

    public async Task<JsonDocument> PostAsync(string path, object? payload = null, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, path);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, options: JsonOptions);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync(response.Content, cancellationToken);
    }

    public async Task<JsonDocument> PatchAsync(string path, object payload, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch, path);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync(response.Content, cancellationToken);
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, path);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
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
        GetAsync("/traffic", cancellationToken);

    public Task<JsonDocument> GetMemoryAsync(CancellationToken cancellationToken = default) =>
        GetAsync("/memory", cancellationToken);

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

    public Task<JsonDocument> SelectProxyAsync(string group, string proxy, CancellationToken cancellationToken = default) =>
        PutAsync($"/proxies/{Uri.EscapeDataString(group)}", new { name = proxy }, cancellationToken);

    public Task<JsonDocument> TestDelayAsync(string proxy, Uri url, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        GetAsync($"/proxies/{Uri.EscapeDataString(proxy)}/delay?url={Uri.EscapeDataString(url.ToString())}&timeout={timeoutMilliseconds}", cancellationToken);

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
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {_secret}");
        return socket;
    }

    public async Task<ClientWebSocket> ConnectWebSocketAsync(string path, CancellationToken cancellationToken = default)
    {
        var socket = CreateWebSocket();
        try
        {
            await socket.ConnectAsync(BuildWebSocketUri(path), cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public Uri BuildWebSocketUri(string path)
    {
        var requestUri = new Uri(_controllerUri, path);
        var builder = new UriBuilder(requestUri)
        {
            Scheme = _controllerUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
        };
        return builder.Uri;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_controllerUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secret);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength == 0)
        {
            return JsonDocument.Parse("{}");
        }

        var text = await content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(text) ? JsonDocument.Parse("{}") : JsonDocument.Parse(text);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"Mihomo controller returned {(int)response.StatusCode}: {body}");
    }
}
