using System.Diagnostics;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class MihomoProcessManager : IAsyncDisposable
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _processGate = new();
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
            Process? exitedProcess = null;
            CancellationTokenSource? exitedLifetime = null;
            lock (_processGate)
            {
                if (_process is not null)
                {
                    if (!HasExited(_process))
                    {
                        return;
                    }

                    exitedProcess = _process;
                    exitedLifetime = _lifetimeCts;
                    _process = null;
                    _lifetimeCts = null;
                }
            }

            DisposeProcess(exitedProcess, exitedLifetime);
            State = CoreState.Starting;
            OnStateChanged();
            StartProcess(executablePath, configurationPath, workingDirectory);
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
            State = CoreState.Starting;
            OnStateChanged();
            StartProcess(executablePath, configurationPath, workingDirectory);
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
        Process? process;
        CancellationTokenSource? lifetime;
        lock (_processGate)
        {
            process = _process;
            lifetime = _lifetimeCts;
            State = CoreState.Stopping;
        }

        OnStateChanged();
        lifetime?.Cancel();
        if (process is not null)
        {
            try
            {
                if (!HasExited(process))
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            catch (InvalidOperationException)
            {
                // The process may have exited between HasExited and Kill.
            }
        }

        lock (_processGate)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
            }

            if (ReferenceEquals(_lifetimeCts, lifetime))
            {
                _lifetimeCts = null;
            }

            State = CoreState.Stopped;
        }

        DisposeProcess(process, lifetime);
        OnStateChanged();
    }

    private void StartProcess(string executablePath, string configurationPath, string workingDirectory)
    {
        Directory.CreateDirectory(workingDirectory);
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
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var lifetime = new CancellationTokenSource();
        process.Exited += ProcessExited;
        lock (_processGate)
        {
            _process = process;
            _lifetimeCts = lifetime;
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Unable to start Mihomo.");
            }

            _ = DrainAsync(process.StandardOutput, isError: false, lifetime.Token);
            _ = DrainAsync(process.StandardError, isError: true, lifetime.Token);

            var running = !HasExited(process);
            lock (_processGate)
            {
                if (ReferenceEquals(_process, process))
                {
                    State = running ? CoreState.Running : CoreState.Failed;
                }
            }

            if (!running)
            {
                lifetime.Cancel();
            }

            OnStateChanged();
        }
        catch
        {
            lifetime.Cancel();
            lock (_processGate)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                    _lifetimeCts = null;
                    State = CoreState.Failed;
                }
            }

            DisposeProcess(process, lifetime);
            OnStateChanged();
            throw;
        }
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
        if (sender is not Process process)
        {
            return;
        }

        var shouldNotify = false;
        lock (_processGate)
        {
            if (!ReferenceEquals(_process, process))
            {
                return;
            }

            _lifetimeCts?.Cancel();
            if (State is not (CoreState.Stopping or CoreState.Restarting))
            {
                State = CoreState.Failed;
                shouldNotify = true;
            }
        }

        if (shouldNotify)
        {
            OnStateChanged();
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private void DisposeProcess(Process? process, CancellationTokenSource? lifetime)
    {
        if (process is not null)
        {
            process.Exited -= ProcessExited;
            process.Dispose();
        }

        lifetime?.Dispose();
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, State);
}
