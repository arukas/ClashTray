using System.Text;
using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.Service;

internal sealed record ServiceCoreUpdateResult(bool Succeeded, string? Path = null, string? Error = null);

// Called only while the dispatcher's lifecycle/TUN gate is held. This handler
// owns the update/validation/rollback transaction, never IPC or gate admission.
internal sealed class ServiceCoreUpdateHandler : IDisposable
{
    private readonly AppPaths _paths;
    private readonly MihomoProcessManager _processManager;
    private readonly CoreUpdater _coreUpdater;

    public ServiceCoreUpdateHandler(AppPaths paths, MihomoProcessManager processManager, HttpClient httpClient, string? managedUserSid)
    {
        _paths = paths;
        _processManager = processManager;
        _coreUpdater = new CoreUpdater(paths, httpClient, managedUserSid);
    }

    public void Dispose() => _coreUpdater.Dispose();

    internal static CoreUpdateManifest ValidateCoreUpdatePayload(ServiceCoreUpdatePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        CoreUpdateManifest manifest = new(payload.Version, payload.DownloadUri, payload.Sha256);
        CoreUpdater.ValidateManifest(manifest);
        return manifest;
    }
    public async Task<ServiceCoreUpdateResult> InstallAsync(ServiceCoreUpdatePayload payload, CancellationToken cancellationToken)
    {
        if (_processManager.State == CoreState.Running)
        {
            return new(false, Error: "请先停止 Mihomo 核心再安装更新。");
        }


        bool installed = false;
        try
        {
            CoreUpdateManifest manifest = ValidateCoreUpdatePayload(payload);
            string path = await _coreUpdater.DownloadAndInstallAsync(manifest, cancellationToken);
            installed = true;
            if (!await ValidateInstalledCoreAsync(cancellationToken))
            {
                throw new InvalidDataException("新 Mihomo 核心未通过最小配置健康检查。");
            }

            return new(true, path);
        }
        catch (OperationCanceledException exception)
        {
            if (installed)
            {
                string? rollbackError = await TryRollbackInstalledCoreAsync();
                if (rollbackError is not null)
                {
                    throw new IOException($"核心更新取消，且自动回滚失败：{rollbackError}", exception);
                }
            }

            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or HttpRequestException
            or IOException
            or InvalidOperationException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            string message = $"核心安装失败：{ErrorSanitizer.Sanitize(exception)}";
            if (installed)
            {
                string? rollbackError = await TryRollbackInstalledCoreAsync();
                message = rollbackError is null
                    ? $"{message} 已自动回滚到上一个核心。"
                    : $"{message}；自动回滚失败：{rollbackError}";
            }

            return new(false, Error: message);
        }
    }

    public async Task<ServiceCoreUpdateResult> RollbackAsync(CancellationToken cancellationToken)
    {
        if (_processManager.State == CoreState.Running)
        {
            return new(false, Error: "请先停止 Mihomo 核心再回滚。");
        }

        try
        {
            string path = await _coreUpdater.RollbackLastInstallAsync(cancellationToken);
            await _processManager.StopAsync(CancellationToken.None);
            return new(true, path);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException)
        {
            return new(false, Error: $"核心回滚失败：{ErrorSanitizer.Sanitize(exception)}");
        }
    }

    private async Task<bool> ValidateInstalledCoreAsync(CancellationToken cancellationToken)
    {
        string runtimeDirectory = Path.Combine(_paths.RuntimeRoot, "mihomo");
        Directory.CreateDirectory(runtimeDirectory);
        string configurationPath = Path.Combine(
            runtimeDirectory,
            $"core-healthcheck-{Guid.NewGuid():N}.yaml");
        try
        {
            await File.WriteAllTextAsync(
                configurationPath,
                """
                mixed-port: 7890
                mode: rule
                log-level: silent
                external-controller: 127.0.0.1:19090
                secret: ''
                proxies: []
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            return await _processManager.ValidateAsync(
                _paths.ManagedCoreExecutable,
                configurationPath,
                runtimeDirectory,
                cancellationToken);
        }
        finally
        {
            if (File.Exists(configurationPath))
            {
                File.Delete(configurationPath);
            }
        }
    }

    private async Task<string?> TryRollbackInstalledCoreAsync()
    {
        string? error = null;
        try
        {
            await _coreUpdater.RollbackLastInstallAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException)
        {
            error = ErrorSanitizer.Sanitize(exception);
        }

        try
        {
            await _processManager.StopAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            string sanitized = ErrorSanitizer.Sanitize(exception);
            error = error is null ? sanitized : $"{error}；{sanitized}";
        }

        return error;
    }

}
