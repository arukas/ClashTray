using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

internal static class RuntimeTestHelpers
{
    public static async Task<string> WriteConfigAsync(string root, string name)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, $"# {name}\nmixed-port: 7890\nproxies: []\n");
        return path;
    }

    public static async Task WriteMetadataAsync(AppPaths paths, ConfigurationProfile profile)
    {
        await File.WriteAllTextAsync(
            Path.Combine(paths.ConfigurationsRoot, $"{profile.Id}.json"),
            System.Text.Json.JsonSerializer.Serialize(profile));
    }
}

internal sealed class FakeSystemProxyController : ISystemProxyController
{
    public FakeSystemProxyController(SystemProxyState initialState)
    {
        State = initialState;
    }

    public SystemProxyState State { get; private set; }

    public int EnableCount { get; private set; }

    public int DisableCount { get; private set; }

    public SystemProxyState DetectState() => State;

    public Task EnableAsync(int port, string bypassList, CancellationToken cancellationToken = default)
    {
        EnableCount++;
        State = SystemProxyState.On;
        return Task.CompletedTask;
    }

    public Task DisableAsync(CancellationToken cancellationToken = default)
    {
        DisableCount++;
        State = SystemProxyState.Off;
        return Task.CompletedTask;
    }

    public void SetState(SystemProxyState state) => State = state;
}

internal sealed class AcceptingCandidateValidator : IConfigurationCandidateValidator
{
    public Task ValidateAsync(string candidatePath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class BlockingCandidateValidator : IConfigurationCandidateValidator
{
    private readonly TaskCompletionSource<bool> _releaseValidation = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _blockNextValidation;

    public TaskCompletionSource<bool> ValidationEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void BlockNextValidation() => Volatile.Write(ref _blockNextValidation, 1);

    public void ReleaseValidation() => _releaseValidation.TrySetResult(true);

    public async Task ValidateAsync(
        string candidatePath,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _blockNextValidation, 0) == 1)
        {
            ValidationEntered.TrySetResult(true);
            await _releaseValidation.Task.WaitAsync(cancellationToken);
        }
    }
}

internal sealed class RejectingCandidateValidator : IConfigurationCandidateValidator
{
    public Task ValidateAsync(string candidatePath, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidDataException("candidate rejected"));
}

internal sealed class TestSettingsStore : ISettingsStore
{
    public TestSettingsStore(AppSettings settings)
    {
        Settings = settings;
    }

    public AppSettings Settings { get; private set; }

    public int SaveCount { get; private set; }

    public Task<SettingsLoadResult> LoadWithStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SettingsLoadResult(Settings, SettingsLoadStatus.Loaded, null));

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        Settings = settings;
        return Task.CompletedTask;
    }
}

internal sealed class FailingCandidateValidator : IConfigurationCandidateValidator
{
    public Task ValidateAsync(string candidatePath, CancellationToken cancellationToken = default) =>
        throw new InvalidDataException("candidate rejected by test");
}

internal sealed class LoopbackSubscriptionServer : IDisposable
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed", Justification = "TcpListener has no Dispose; Stop() in Dispose releases the socket.")]
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _dispose = new();
    private readonly byte[] _body;

    public LoopbackSubscriptionServer(byte[] body)
    {
        _body = body;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(ServeAsync);
    }

    public int Port { get; }

    private async Task ServeAsync()
    {
        try
        {
            while (!_dispose.IsCancellationRequested)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_dispose.Token);
                using NetworkStream stream = client.GetStream();
                byte[] buffer = new byte[4096];
                int total = 0;
                while (total < buffer.Length)
                {
                    int read = await stream.ReadAsync(
                        buffer.AsMemory(total, buffer.Length - total),
                        _dispose.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                string header =
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/yaml\r\nContent-Length: {_body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _dispose.Token);
                await stream.WriteAsync(_body, _dispose.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _dispose.Cancel();
        _listener.Stop();
        _dispose.Dispose();
    }
}

internal sealed class BlockingSettingsStore : ISettingsStore
{
    private readonly TaskCompletionSource<bool> _releaseSave = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _blockNextSave;

    public BlockingSettingsStore(AppSettings settings)
    {
        Settings = settings;
    }

    public AppSettings Settings { get; private set; }

    public TaskCompletionSource<bool> SaveEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void BlockNextSave() => Volatile.Write(ref _blockNextSave, 1);

    public void ReleaseSave() => _releaseSave.TrySetResult(true);

    public Task<SettingsLoadResult> LoadWithStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SettingsLoadResult(Settings, SettingsLoadStatus.Loaded, null));

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _blockNextSave, 0) == 1)
        {
            SaveEntered.TrySetResult(true);
            await _releaseSave.Task.WaitAsync(cancellationToken);
        }

        Settings = settings;
    }
}

internal sealed class ProxySwitchHandler : HttpMessageHandler
{
    public List<string> Operations { get; } = [];

    public bool FailCloseAll { get; init; }

    public string CurrentProxy { get; private set; } = "old";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (request.Method == HttpMethod.Put && path.StartsWith("/proxies/", StringComparison.Ordinal))
        {
            Operations.Add("select");
            CurrentProxy = "new";
            return Task.FromResult(JsonResponse("{}"));
        }

        if (request.Method == HttpMethod.Delete && path == "/connections")
        {
            Operations.Add("close");
            return Task.FromResult(FailCloseAll
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.NoContent));
        }

        if (request.Method == HttpMethod.Get && path == "/proxies")
        {
            string body = "{\"proxies\":{\"Auto\":{\"type\":\"Selector\",\"now\":\""
                + CurrentProxy
                + "\",\"all\":[\"old\",\"new\"]},\"old\":{\"type\":\"Direct\"},\"new\":{\"type\":\"Direct\"}}}";
            return Task.FromResult(JsonResponse(body));
        }

        string responseBody = path switch
        {
            "/version" => "{\"version\":\"v1.19.30\"}",
            "/configs" => "{\"mode\":\"rule\",\"tun\":{\"enable\":false}}",
            "/traffic" => "{\"upTotal\":0,\"downTotal\":0,\"up\":0,\"down\":0}",
            "/memory" => "{\"inuse\":0}",
            "/connections" => "{\"connections\":[]}",
            "/rules" => "{\"rules\":[]}",
            "/providers/proxies" => "{\"providers\":{}}",
            "/providers/rules" => "{\"providers\":{}}",
            _ => "{}"
        };
        return Task.FromResult(JsonResponse(responseBody));
    }

    private static HttpResponseMessage JsonResponse(string body) =>
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        };
}

internal sealed class BlockingInstallService : IServicePipeClient
{
    private readonly TaskCompletionSource<bool> _releaseInstall = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> InstallEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int InstallRequestCount { get; private set; }

    public void ReleaseInstall() => _releaseInstall.TrySetResult(true);

    public async Task<ServiceResponse> SendAsync(
        ServiceCommand command,
        string? payload = null,
        CancellationToken cancellationToken = default)
    {
        if (command == ServiceCommand.InstallCore)
        {
            InstallRequestCount++;
            InstallEntered.TrySetResult(true);
            await _releaseInstall.Task.WaitAsync(cancellationToken);
        }

        return new ServiceResponse(
            Guid.NewGuid(),
            true,
            TunState.Off,
            Payload: "mihomo.exe");
    }
}

internal sealed class RuntimeControllerHandler : HttpMessageHandler
{
    public ConcurrentQueue<string> RequestedPaths { get; } = new ConcurrentQueue<string>();

    public bool FailMetrics { get; set; }

    public bool FailNextAllowLanEnable { get; set; }

    public bool HoldFirstVersionRequest { get; set; }

    public TaskCompletionSource<bool> FirstVersionRequestEntered { get; } =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> ModePatchEntered { get; } =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource<bool> ReleaseFirstVersionRequestSource { get; } =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool AllowLan { get; private set; }

    public bool Ipv6 { get; private set; } = true;

    public int NetworkPatchCount { get; private set; }

    public int ModePatchCount { get; private set; }

    public ProxyMode Mode { get; private set; } = ProxyMode.Rule;

    public void ReleaseFirstVersionRequest() => ReleaseFirstVersionRequestSource.TrySetResult(true);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? path = request.RequestUri?.AbsolutePath;
        if (path is not null)
        {
            RequestedPaths.Enqueue(path);
        }

        if (HoldFirstVersionRequest
            && path == "/version"
            && !FirstVersionRequestEntered.Task.IsCompleted)
        {
            FirstVersionRequestEntered.TrySetResult(true);
            await ReleaseFirstVersionRequestSource.Task.WaitAsync(cancellationToken);
        }

        if (FailMetrics && path is "/traffic" or "/memory")
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("redacted")
            };
        }

        if (request.Method == HttpMethod.Patch && path == "/configs")
        {
            using JsonDocument payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (payload.RootElement.TryGetProperty("allow-lan", out JsonElement allowLan))
            {
                if (FailNextAllowLanEnable && allowLan.GetBoolean())
                {
                    FailNextAllowLanEnable = false;
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("simulated network settings failure")
                    };
                }

                AllowLan = allowLan.GetBoolean();
            }

            if (payload.RootElement.TryGetProperty("ipv6", out JsonElement ipv6))
            {
                Ipv6 = ipv6.GetBoolean();
            }

            if (payload.RootElement.TryGetProperty("mode", out JsonElement mode))
            {
                ModePatchEntered.TrySetResult(true);
                Mode = Enum.Parse<ProxyMode>(mode.GetString()!, ignoreCase: true);
                ModePatchCount++;
            }

            NetworkPatchCount++;
            return new HttpResponseMessage(HttpStatusCode.NoContent)
            {
                Content = new StringContent(string.Empty)
            };
        }

        string modeText = Mode switch
        {
            ProxyMode.Rule => "rule",
            ProxyMode.Global => "global",
            ProxyMode.Direct => "direct",
            _ => "rule"
        };
        string body = path switch
        {
            "/version" => "{\"version\":\"v1.19.30\"}",
            "/configs" => $"{{\"mode\":\"{modeText}\",\"allow-lan\":{(AllowLan ? "true" : "false")},\"ipv6\":{(Ipv6 ? "true" : "false")},\"tun\":{{\"enable\":true}}}}",
            "/proxies" => "{\"proxies\":{}}",
            "/traffic" => "{\"upTotal\":11,\"downTotal\":12,\"up\":1,\"down\":2}\n",
            "/memory" => "{\"inuse\":22}\n",
            "/connections" => "{\"connections\":[]}",
            "/rules" => "{\"rules\":[]}",
            "/providers/proxies" => "{\"providers\":{}}",
            "/providers/rules" => "{\"providers\":{}}",
            _ => "{}"
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        };
    }
}

internal sealed class DelayControllerHandler : HttpMessageHandler
{
    private readonly bool _holdFirstRequest;
    private readonly TaskCompletionSource<bool> _releaseFirstRequest =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action? _beforeFirstRequest;
    private int _requestCount;
    private int _inFlight;
    private int _maxInFlight;

    public DelayControllerHandler(bool holdFirstRequest)
    {
        _holdFirstRequest = holdFirstRequest;
    }

    public TaskCompletionSource<bool> FirstRequestEntered { get; } =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> ModePatchEntered { get; } =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    public Action? BeforeFirstRequest
    {
        get => _beforeFirstRequest;
        set => _beforeFirstRequest = value;
    }

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    public void ReleaseFirstRequest() => _releaseFirstRequest.TrySetResult(true);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (request.Method == HttpMethod.Patch && path == "/configs")
        {
            ModePatchEntered.TrySetResult(true);
            return new HttpResponseMessage(HttpStatusCode.NoContent)
            {
                Content = new StringContent(string.Empty)
            };
        }

        if (!path.StartsWith("/proxies/", StringComparison.Ordinal))
        {
            string body = path switch
            {
                "/configs" => "{\"mode\":\"direct\",\"tun\":{\"enable\":false}}",
                _ => "{}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            };
        }

        int requestNumber = Interlocked.Increment(ref _requestCount);
        if (requestNumber == 1)
        {
            Action? callback = Interlocked.Exchange(ref _beforeFirstRequest, null);
            callback?.Invoke();
        }

        int current = Interlocked.Increment(ref _inFlight);
        UpdateMaximum(current);
        try
        {
            if (_holdFirstRequest && requestNumber == 1)
            {
                FirstRequestEntered.TrySetResult(true);
                await _releaseFirstRequest.Task.WaitAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"delay\":10}")
            };
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private void UpdateMaximum(int current)
    {
        while (true)
        {
            int previous = Volatile.Read(ref _maxInFlight);
            if (current <= previous
                || Interlocked.CompareExchange(ref _maxInFlight, current, previous) == previous)
            {
                return;
            }
        }
    }
}
