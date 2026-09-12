using System.Diagnostics;
using System.Text;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class MihomoProcessManager : IAsyncDisposable
{
    private const int MaxLogLineCharacters = 64 * 1024;
    private static readonly TimeSpan DefaultValidationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _processGate = new();
    private readonly TimeSpan _validationTimeout;
    private readonly TimeSpan _stopTimeout;
    private Process? _process;
    private CancellationTokenSource? _lifetimeCts;

    public CoreState State { get; private set; } = CoreState.Stopped;

    public event EventHandler<CoreState>? StateChanged;

    public event Action<string, bool>? LogLineReceived;

    public MihomoProcessManager()
        : this(DefaultValidationTimeout, DefaultStopTimeout)
    {
    }

    internal MihomoProcessManager(TimeSpan validationTimeout, TimeSpan stopTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(validationTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stopTimeout, TimeSpan.Zero);

        _validationTimeout = validationTimeout;
        _stopTimeout = stopTimeout;
    }

    public async Task<bool> ValidateAsync(string executablePath, string configurationPath, CancellationToken cancellationToken = default)
        => await ValidateAsync(executablePath, configurationPath, workingDirectory: null, cancellationToken);

    public async Task<bool> ValidateAsync(
        string executablePath,
        string configurationPath,
        string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            State = CoreState.Validating;
            OnStateChanged();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_validationTimeout);
            try
            {
                int result = await RunOneShotAsync(
                    executablePath,
                    $"-t -f \"{configurationPath}\"",
                    workingDirectory,
                    timeout.Token);
                State = result == 0 ? CoreState.Stopped : CoreState.Failed;
                OnStateChanged();
                return result == 0;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                State = CoreState.Failed;
                OnStateChanged();
                throw new TimeoutException($"Mihomo 配置验证超过 {_validationTimeout.TotalSeconds:0} 秒。");
            }
            catch (OperationCanceledException)
            {
                State = CoreState.Stopped;
                OnStateChanged();
                throw;
            }
            catch
            {
                State = CoreState.Failed;
                OnStateChanged();
                throw;
            }
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
        if (lifetime is not null)
        {
            await lifetime.CancelAsync();
        }

        bool stopped = process is null;
        Exception? stopException = null;
        if (process is not null)
        {
            try
            {
                if (!HasExited(process))
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(_stopTimeout);
                }

                stopped = true;
            }
            catch (InvalidOperationException)
            {
                // The process may have exited between HasExited and Kill.
                stopped = true;
            }
            catch (TimeoutException exception)
            {
                stopException = exception;
                stopped = HasExited(process);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                stopException = exception;
                stopped = HasExited(process);
            }
        }

        lock (_processGate)
        {
            if (stopped && ReferenceEquals(_process, process))
            {
                _process = null;
            }

            if (stopped && ReferenceEquals(_lifetimeCts, lifetime))
            {
                _lifetimeCts = null;
            }

            State = stopped ? CoreState.Stopped : CoreState.Failed;
        }

        if (stopped)
        {
            DisposeProcess(process, lifetime);
        }

        OnStateChanged();
        if (stopException is not null)
        {
            throw new TimeoutException(
                $"停止 Mihomo 进程超过 {_stopTimeout.TotalSeconds:0} 秒，进程仍可重试停止。",
                stopException);
        }
    }

    private void StartProcess(string executablePath, string configurationPath, string workingDirectory)
    {
        Directory.CreateDirectory(workingDirectory);
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = $"-d \"{workingDirectory}\" -f \"{configurationPath}\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        Process process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        CancellationTokenSource lifetime = new CancellationTokenSource();
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

            bool running = !HasExited(process);
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

    private static async Task<int> RunOneShotAsync(
        string executablePath,
        string arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Mihomo validation.");
        Task standardOutput = DrainValidationOutputAsync(process.StandardOutput);
        Task standardError = DrainValidationOutputAsync(process.StandardError);
        bool completed = false;
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(standardOutput, standardError);
            completed = true;
            return process.ExitCode;
        }
        finally
        {
            if (!completed)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }

                try
                {
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                }

                await Task.WhenAll(standardOutput, standardError);
            }
        }
    }

    private static async Task DrainValidationOutputAsync(StreamReader reader)
    {
        char[] buffer = new char[8 * 1024];
        while (await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None) > 0)
        {
        }
    }

    private async Task DrainAsync(StreamReader reader, bool isError, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await ReadLineLimitedAsync(reader, cancellationToken);
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

    private static async Task<string?> ReadLineLimitedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        StringBuilder builder = new StringBuilder(Math.Min(MaxLogLineCharacters, 1024));
        char[] buffer = new char[1024];
        bool truncated = false;
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
            {
                if (builder.Length == 0 && !truncated)
                {
                    return null;
                }

                break;
            }

            for (int index = 0; index < count; index++)
            {
                char character = buffer[index];
                if (character == '\n')
                {
                    return FormatLogLine(builder, truncated);
                }

                if (!truncated)
                {
                    if (builder.Length < MaxLogLineCharacters)
                    {
                        builder.Append(character);
                    }
                    else
                    {
                        truncated = true;
                    }
                }
            }
        }

        return FormatLogLine(builder, truncated);
    }

    private static string FormatLogLine(StringBuilder builder, bool truncated) =>
        truncated
            ? $"{builder.ToString().TrimEnd('\r')} … [日志行已截断]"
            : builder.ToString().TrimEnd('\r');

    private void ProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process process)
        {
            return;
        }

        bool shouldNotify = false;
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
