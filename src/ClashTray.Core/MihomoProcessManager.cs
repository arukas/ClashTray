using System.Diagnostics;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class MihomoProcessManager : IAsyncDisposable
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private Process? _process;
    private CancellationTokenSource? _lifetimeCts;

    public CoreState State { get; private set; } = CoreState.Stopped;

    public event EventHandler<CoreState>? StateChanged;

    public event Action<string, bool>? LogLineReceived;

    public async Task<bool> ValidateAsync(string executablePath, string configurationPath, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            State = CoreState.Validating;
            OnStateChanged();
            var result = await RunOneShotAsync(executablePath, $"-t -f \"{configurationPath}\"", cancellationToken);
            State = result == 0 ? CoreState.Stopped : CoreState.Failed;
            OnStateChanged();
            return result == 0;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task StartAsync(
        string executablePath,
        string configurationPath,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false })
            {
                return;
            }

            State = CoreState.Starting;
            OnStateChanged();
            Directory.CreateDirectory(workingDirectory);
            _lifetimeCts?.Dispose();
            _lifetimeCts = new CancellationTokenSource();

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"-d \"{workingDirectory}\" -f \"{configurationPath}\"",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.Exited += ProcessExited;
            if (!_process.Start())
            {
                State = CoreState.Failed;
                OnStateChanged();
                throw new InvalidOperationException("Unable to start Mihomo.");
            }

            _ = DrainAsync(_process.StandardOutput, isError: false, cancellationToken: _lifetimeCts.Token);
            _ = DrainAsync(_process.StandardError, isError: true, cancellationToken: _lifetimeCts.Token);
            State = CoreState.Running;
            OnStateChanged();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task RestartAsync(
        string executablePath,
        string configurationPath,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            State = CoreState.Restarting;
            OnStateChanged();
            await StopCoreAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"-d \"{workingDirectory}\" -f \"{configurationPath}\"",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.Exited += ProcessExited;
            if (!_process.Start())
            {
                State = CoreState.Failed;
                OnStateChanged();
                throw new InvalidOperationException("Unable to restart Mihomo.");
            }

            _lifetimeCts?.Dispose();
            _lifetimeCts = new CancellationTokenSource();
            _ = DrainAsync(_process.StandardOutput, isError: false, cancellationToken: _lifetimeCts.Token);
            _ = DrainAsync(_process.StandardError, isError: true, cancellationToken: _lifetimeCts.Token);
            State = CoreState.Running;
            OnStateChanged();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _operationLock.Dispose();
        _lifetimeCts?.Dispose();
    }

    private async Task StopCoreAsync()
    {
        if (_process is null)
        {
            State = CoreState.Stopped;
            OnStateChanged();
            return;
        }

        State = CoreState.Stopping;
        OnStateChanged();
        _lifetimeCts?.Cancel();
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process.Exited -= ProcessExited;
        _process.Dispose();
        _process = null;
        State = CoreState.Stopped;
        OnStateChanged();
    }

    private static async Task<int> RunOneShotAsync(string executablePath, string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Mihomo validation.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(standardOutput, standardError);
        return process.ExitCode;
    }

    private async Task DrainAsync(StreamReader reader, bool isError, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    LogLineReceived?.Invoke(line, isError);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ProcessExited(object? sender, EventArgs e)
    {
        if (State is not (CoreState.Stopping or CoreState.Restarting))
        {
            State = CoreState.Failed;
            OnStateChanged();
        }
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, State);
}
