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
                ServiceCommand.GetStatus => Success(request),
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
        _tunState = TunState.Off;
        return Success(request);
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
        if (payload.ControllerPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(payload.ControllerSecret))
        {
            return Failure(request, "TUN 请求参数无效。", _processManager.State);
        }

        if (_api is null || _activeCore is null || _activeCore.ControllerPort != payload.ControllerPort)
        {
            _api = CreateApi(payload.ControllerPort, payload.ControllerSecret);
        }

        if (_api is null)
        {
            return Failure(request, "Mihomo 核心尚未运行。", _processManager.State);
        }

        await _api.SetTunAsync(enabled, cancellationToken);
        var confirmed = await ConfirmTunStateAsync(_api, enabled, cancellationToken);
        if (!confirmed)
        {
            _tunState = TunState.Failed;
            return Failure(request, "Mihomo 未确认 TUN 状态变更。", _processManager.State);
        }

        _tunState = enabled ? TunState.On : TunState.Off;
        return Success(request);
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

    private MihomoApiClient CreateApi(int port, string secret) =>
        new(_httpClient, new Uri($"http://127.0.0.1:{port}/"), secret);

    private static void ValidateCorePayload(ServiceCorePayload payload)
    {
        if (!IsAllowedCoreExecutable(payload.ExecutablePath)
            || !IsAllowedRuntimePath(payload.ConfigurationPath, allowYaml: true)
            || !IsAllowedRuntimePath(payload.WorkingDirectory, allowYaml: false)
            || payload.ControllerPort is < 1 or > 65535
            || string.IsNullOrWhiteSpace(payload.ControllerSecret))
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

    private ServiceResponse Success(ServiceRequest request) =>
        new(request.RequestId, true, _tunState, Core: CoreState);

    private ServiceResponse Failure(ServiceRequest request, string error, CoreState? core = null) =>
        new(request.RequestId, false, _tunState, Error: error, Core: core ?? CoreState);

    private T Deserialize<T>(string? payload) where T : class =>
        string.IsNullOrWhiteSpace(payload)
            ? throw new InvalidDataException("服务请求缺少参数。")
            : JsonSerializer.Deserialize<T>(payload, _jsonOptions)
              ?? throw new InvalidDataException("服务请求参数无效。");
}
