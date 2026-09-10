using System.Text.Json;
using System.Diagnostics;
using Microsoft.Win32;
using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.Service;

/// <summary>
/// The privileged boundary accepts only typed, allow-listed lifecycle and TUN requests.
/// It never accepts a shell command or an arbitrary executable path.
/// </summary>
internal sealed class ServiceRuntimeController : IAsyncDisposable
{
    private const string ProfileListPath = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList";
    private static readonly TimeSpan StatusQueryTimeout = TimeSpan.FromSeconds(2);
    private readonly MihomoProcessManager _processManager = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private MihomoApiClient? _api;
    private TunState _tunState = TunState.Off;
    private ServiceCorePayload? _activeCore;

    public CoreState CoreState => _processManager.State;

    public async Task<ServiceResponse> HandleAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return request.Command switch
            {
                ServiceCommand.GetStatus => await GetStatusAsync(request, cancellationToken),
                ServiceCommand.StartCore => await StartCoreAsync(request, cancellationToken),
                ServiceCommand.StopCore => await StopCoreAsync(request, cancellationToken),
                ServiceCommand.RestartCore => await RestartCoreAsync(request, cancellationToken),
                ServiceCommand.EnableTun => await SetTunAsync(request, enabled: true, cancellationToken),
                ServiceCommand.DisableTun => await SetTunAsync(request, enabled: false, cancellationToken),
                _ => Failure(request, "未知服务命令。")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(request, exception.Message);
        }
    }

    private async Task<ServiceResponse> GetStatusAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (_processManager.State != CoreState.Running || _api is null || _activeCore is null)
        {
            _tunState = TunState.Off;
            return Success(request);
        }

        using var timeout = CreateTimeout(StatusQueryTimeout, cancellationToken);
        try
        {
            var value = await ReadTunStateAsync(_api, timeout.Token);
            if (value is not bool enabled)
            {
                _tunState = TunState.Unknown;
                return Failure(request, "无法从 Mihomo 控制器确认 TUN 状态。", CoreState.Running);
            }

            _tunState = enabled ? TunState.On : TunState.Off;
            return Success(request);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _tunState = TunState.Unknown;
            return Failure(request, "查询 Mihomo TUN 状态超时，状态暂时无法确认。", CoreState.Running);
        }
        catch (Exception exception)
        {
            _tunState = TunState.Unknown;
            return Failure(request, $"查询 Mihomo TUN 状态失败，状态暂时无法确认：{DescribeControllerError(exception)}", CoreState.Running);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_api is not null)
            {
                try
                {
                    await _api.SetTunAsync(false);
                }
                catch
                {
                }
            }

            await _processManager.DisposeAsync();
            if (!IsDesktopProcessRunning())
            {
                SystemProxyRecovery.RestoreOwnedStatesForLoadedUsers();
            }
        }
        finally
        {
            _httpClient.Dispose();
        }
    }

    private static bool IsDesktopProcessRunning()
    {
        try
        {
            return Process.GetProcessesByName("ClashTray.App").Any(process =>
            {
                try
                {
                    return process.SessionId != 0;
                }
                finally
                {
                    process.Dispose();
                }
            });
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<ServiceResponse> StartCoreAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        var payload = Deserialize<ServiceCorePayload>(request.Payload);
        ValidateCorePayload(payload);
        if (_processManager.State == CoreState.Running)
        {
            return Success(request);
        }

        if (!await _processManager.ValidateAsync(payload.ExecutablePath, payload.ConfigurationPath, cancellationToken))
        {
            return Failure(request, "Mihomo 配置验证失败。", CoreState.Failed);
        }

        await _processManager.StartAsync(payload.ExecutablePath, payload.ConfigurationPath, payload.WorkingDirectory, cancellationToken);
        _activeCore = payload;
        _api = CreateApi(payload.ControllerPort, payload.ControllerSecret);
        _tunState = TunState.Unknown;
        return await RefreshTunStateAfterStartAsync(request, cancellationToken);
    }

    private async Task<ServiceResponse> StopCoreAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (_api is not null)
        {
            try
            {
                await _api.SetTunAsync(false, cancellationToken);
            }
            catch
            {
            }
        }

        await _processManager.StopAsync(cancellationToken);
        _api = null;
        _activeCore = null;
        _tunState = TunState.Off;
        return Success(request);
    }

    private async Task<ServiceResponse> RestartCoreAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        var payload = Deserialize<ServiceCorePayload>(request.Payload);
        ValidateCorePayload(payload);
        await StopCoreAsync(request, cancellationToken);
        return await StartCoreAsync(request with { Payload = JsonSerializer.Serialize(payload, _jsonOptions) }, cancellationToken);
    }

    private async Task<ServiceResponse> SetTunAsync(ServiceRequest request, bool enabled, CancellationToken cancellationToken)
    {
        var payload = Deserialize<ServiceTunPayload>(request.Payload);
        if (payload.ControllerPort is < 1 or > 65535 || payload.ControllerSecret is null)
        {
            return Failure(request, "TUN 请求参数无效。", _processManager.State);
        }

        var api = _api;
        var activeCore = _activeCore;
        if (_processManager.State != CoreState.Running || api is null || activeCore is null)
        {
            return Failure(request, "Mihomo 核心尚未运行。", _processManager.State);
        }

        if (activeCore.ControllerPort != payload.ControllerPort
            || !string.Equals(activeCore.ControllerSecret, payload.ControllerSecret, StringComparison.Ordinal))
        {
            return Failure(request, "TUN 请求与当前 Mihomo 核心不匹配。", _processManager.State);
        }

        bool? previous;
        try
        {
            previous = await ReadTunStateAsync(api, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _tunState = TunState.Unknown;
            return Failure(request, $"无法确认当前 TUN 状态，未执行变更：{DescribeControllerError(exception)}", _processManager.State);
        }

        if (previous is null)
        {
            _tunState = TunState.Unknown;
            return Failure(request, "无法确认当前 TUN 状态，未执行变更。", _processManager.State);
        }

        var previousValue = previous.Value;
        if (previousValue == enabled)
        {
            _tunState = enabled ? TunState.On : TunState.Off;
            return Success(request);
        }

        try
        {
            await api.SetTunAsync(enabled, cancellationToken);
            if (!await ConfirmTunStateAsync(api, enabled, cancellationToken))
            {
                throw new InvalidOperationException("Mihomo 未确认 TUN 状态变更。");
            }

            _tunState = enabled ? TunState.On : TunState.Off;
            return Success(request);
        }
        catch (OperationCanceledException)
        {
            var restored = await TryRestoreTunStateAsync(api, previousValue);
            _tunState = restored
                ? previousValue ? TunState.On : TunState.Off
                : TunState.Unknown;
            throw;
        }
        catch (Exception exception)
        {
            var restored = await TryRestoreTunStateAsync(api, previousValue);
            _tunState = restored
                ? previousValue ? TunState.On : TunState.Off
                : TunState.Unknown;
            var message = restored
                ? $"TUN 操作失败，已恢复原状态：{exception.Message}"
                : $"TUN 操作失败，且无法确认原状态：{DescribeControllerError(exception)}";
            return Failure(request, message, _processManager.State);
        }
    }

    private async Task<ServiceResponse> RefreshTunStateAfterStartAsync(
        ServiceRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(StatusQueryTimeout, cancellationToken);
        try
        {
            var value = await ReadTunStateAsync(_api!, timeout.Token);
            if (value is bool enabled)
            {
                _tunState = enabled ? TunState.On : TunState.Off;
                return Success(request);
            }

            _tunState = TunState.Unknown;
            return Success(request, "Mihomo 已启动，但 TUN 状态暂时无法确认。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _tunState = TunState.Unknown;
            return Success(request, "Mihomo 已启动，但 TUN 状态查询超时，暂时无法确认。");
        }
        catch (Exception exception)
        {
            _tunState = TunState.Unknown;
            return Success(request, $"Mihomo 已启动，但 TUN 状态暂时无法确认：{DescribeControllerError(exception)}");
        }
    }

    private static CancellationTokenSource CreateTimeout(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private static string DescribeControllerError(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } statusCode } => $"HTTP {(int)statusCode}",
        MihomoStreamException streamException => $"{streamException.Path} {streamException.Kind}",
        TimeoutException => "超时",
        _ => exception.GetType().Name
    };

    private static async Task<bool?> ReadTunStateAsync(MihomoApiClient api, CancellationToken cancellationToken)
    {
        using var document = await api.GetConfigurationAsync(force: false, cancellationToken);
        return MihomoDataParser.ParseTunEnabled(document);
    }

    private static async Task<bool> ConfirmTunStateAsync(MihomoApiClient api, bool expected, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var document = await api.GetConfigurationAsync(force: false, cancellationToken);
            var value = MihomoDataParser.ParseTunEnabled(document);
            if (value == expected)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        return false;
    }

    private static async Task<bool> TryRestoreTunStateAsync(MihomoApiClient api, bool expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await api.SetTunAsync(expected, timeout.Token);
            return await ConfirmTunStateAsync(api, expected, timeout.Token);
        }
        catch
        {
            return false;
        }
    }

    private MihomoApiClient CreateApi(int port, string secret) =>
        new(_httpClient, new Uri($"http://127.0.0.1:{port}/"), secret);

    private static void ValidateCorePayload(ServiceCorePayload payload)
    {
        if (!IsAllowedCoreExecutable(payload.ExecutablePath)
            || !IsAllowedRuntimePath(payload.ConfigurationPath, allowYaml: true)
            || !IsAllowedRuntimePath(payload.WorkingDirectory, allowYaml: false)
            || payload.ControllerPort is < 1 or > 65535
            || payload.ControllerSecret is null)
        {
            throw new InvalidOperationException("服务拒绝了不受信任的核心路径或参数。");
        }
    }

    private static bool IsAllowedCoreExecutable(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        return File.Exists(fullPath)
            && string.Equals(Path.GetFileName(fullPath), "mihomo.exe", StringComparison.OrdinalIgnoreCase)
            && directory is not null
            && IsAllowedCoreDirectory(directory);
    }

    private static bool IsAllowedRuntimePath(string path, bool allowYaml)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
        if (directory is null || !IsAllowedRuntimeDirectory(directory))
        {
            return false;
        }

        return !allowYaml || Path.GetExtension(fullPath) is ".yaml" or ".yml";
    }

    private static bool IsAllowedCoreDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var programDataCore = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ClashTray",
            "core");
        if (PathEquals(fullPath, programDataCore))
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var profiles = Registry.LocalMachine.OpenSubKey(ProfileListPath, writable: false);
        if (profiles is null)
        {
            return false;
        }

        foreach (var sid in profiles.GetSubKeyNames())
        {
            using var profile = profiles.OpenSubKey(sid, writable: false);
            var profilePath = profile?.GetValue("ProfileImagePath") as string;
            if (string.IsNullOrWhiteSpace(profilePath))
            {
                continue;
            }

            var localCore = Path.Combine(
                Environment.ExpandEnvironmentVariables(profilePath),
                "AppData",
                "Local",
                "ClashTray",
                "core");
            if (PathEquals(fullPath, localCore))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAllowedRuntimeDirectory(string path)
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ClashTray",
            "runtime",
            "mihomo");
        return PathEquals(path, expected);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private ServiceResponse Success(ServiceRequest request, string? error = null) =>
        new(request.RequestId, true, _tunState, Error: error, Core: CoreState);

    private ServiceResponse Failure(ServiceRequest request, string error, CoreState? core = null) =>
        new(request.RequestId, false, _tunState, Error: error, Core: core ?? CoreState);

    private T Deserialize<T>(string? payload) where T : class =>
        string.IsNullOrWhiteSpace(payload)
            ? throw new InvalidDataException("服务请求缺少参数。")
            : JsonSerializer.Deserialize<T>(payload, _jsonOptions)
              ?? throw new InvalidDataException("服务请求参数无效。");
}
