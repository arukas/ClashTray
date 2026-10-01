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
    private readonly object _stateEventGate = new();
    private readonly TimeSpan _validationTimeout;
    private readonly TimeSpan _stopTimeout;
    private readonly Func<ProcessStartInfo, Process> _processStartInfoFactory;
    private readonly Func<ProcessStartInfo, Process> _validationProcessFactory;
    private Process? _process;
    private CancellationTokenSource? _lifetimeCts;
    private ProcessJobObject? _processJob;
    private long _generation;
    private Guid _instanceId;
    private readonly Guid _ownerInstanceId = Guid.NewGuid();

    public CoreState State { get; private set; } = CoreState.Stopped;

    /// <summary>
    /// Monotonically increasing process generation. Controller and TUN
    /// transactions capture this value so a late response from an older
    /// Mihomo process cannot commit state for a newer process.
    /// </summary>
    public long Generation => Interlocked.Read(ref _generation);

    public Guid InstanceId
    {
        get
        {
            lock (_processGate)
            {
                return _instanceId;
            }
        }
    }

    public Guid OwnerInstanceId => _ownerInstanceId;

    public event EventHandler<CoreState>? StateChanged;

    public event Action<string, bool>? LogLineReceived;

    public MihomoProcessManager()
        : this(DefaultValidationTimeout, DefaultStopTimeout)
    {
    }

    internal MihomoProcessManager(TimeSpan validationTimeout, TimeSpan stopTimeout)
        : this(validationTimeout, stopTimeout, null)
    {
    }

    internal MihomoProcessManager(
        TimeSpan validationTimeout,
        TimeSpan stopTimeout,
        Func<ProcessStartInfo, Process>? processStartInfoFactory,
        Func<ProcessStartInfo, Process>? validationProcessFactory = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(validationTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stopTimeout, TimeSpan.Zero);

        _validationTimeout = validationTimeout;
        _stopTimeout = stopTimeout;
        _processStartInfoFactory = processStartInfoFactory ?? (startInfo => new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        });
        _validationProcessFactory = validationProcessFactory ?? (startInfo => new Process { StartInfo = startInfo });
    }

    public Task<bool> ValidateAsync(string executablePath, string configurationPath, CancellationToken cancellationToken = default)
        => ValidateAsync(
            executablePath,
            configurationPath,
            workingDirectory: null,
            safePaths: null,
            cancellationToken: cancellationToken);

    public Task<bool> ValidateAsync(
        string executablePath,
        string configurationPath,
        string? workingDirectory,
        CancellationToken cancellationToken = default)
        => ValidateAsync(
            executablePath,
            configurationPath,
            workingDirectory,
            safePaths: null,
            cancellationToken: cancellationToken);

    public async Task<bool> ValidateAsync(
        string executablePath,
        string configurationPath,
        string? workingDirectory,
        string? safePaths,
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
                    safePaths,
                    timeout.Token);
                State = result == 0 ? CoreState.Stopped : CoreState.Failed;
                OnStateChanged();
                return result == 0;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                State = CoreState.Failed;
                OnStateChanged();
                throw new TimeoutException($"Mihomo 配置验证超过 {_validationTimeout.TotalSeconds:0} 秒。", exception);
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

    public Task StartAsync(
        string executablePath,
        string configurationPath,
        string workingDirectory,
        CancellationToken cancellationToken = default)
        => StartAsync(
            executablePath,
            configurationPath,
            workingDirectory,
            safePaths: null,
            cancellationToken: cancellationToken);

    public async Task StartAsync(
        string executablePath,
        string configurationPath,
        string workingDirectory,
        string? safePaths,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            Process? exitedProcess = null;
            CancellationTokenSource? exitedLifetime = null;
            ProcessJobObject? exitedJob = null;
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
                    exitedJob = _processJob;
                    _process = null;
                    _lifetimeCts = null;
                    _processJob = null;
                }
            }

            DisposeProcess(exitedProcess, exitedLifetime, exitedJob);
            State = CoreState.Starting;
            OnStateChanged();
            StartProcess(executablePath, configurationPath, workingDirectory, safePaths);
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

    internal bool TryMarkReady(long expectedGeneration)
    {
        lock (_processGate)
        {
            if (expectedGeneration != Generation
                || _process is null
                || HasExited(_process))
            {
                return false;
            }

            State = CoreState.Running;
        }

        OnStateChanged();
        return State == CoreState.Running && expectedGeneration == Generation;
    }

    public Task RestartAsync(
        string executablePath,
        string configurationPath,
        string workingDirectory,
        CancellationToken cancellationToken = default)
        => RestartAsync(
            executablePath,
            configurationPath,
            workingDirectory,
            safePaths: null,
            cancellationToken: cancellationToken);

    public async Task RestartAsync(
        string executablePath,
        string configurationPath,
        string workingDirectory,
        string? safePaths,
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
            StartProcess(executablePath, configurationPath, workingDirectory, safePaths);
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
        ProcessJobObject? job;
        lock (_processGate)
        {
            process = _process;
            lifetime = _lifetimeCts;
            job = _processJob;
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

            if (stopped && ReferenceEquals(_processJob, job))
            {
                _processJob = null;
            }

            if (stopped)
            {
                _instanceId = Guid.Empty;
            }

            State = stopped ? CoreState.Stopped : CoreState.Failed;
        }

        if (stopped)
        {
            DisposeProcess(process, lifetime, job);
        }

        OnStateChanged();
        if (stopException is not null)
        {
            throw new TimeoutException(
                $"停止 Mihomo 进程超过 {_stopTimeout.TotalSeconds:0} 秒，进程仍可重试停止。",
                stopException);
        }
    }

    internal LocalCoreProcessIdentity? CaptureRunningProcessIdentity()
    {
        lock (_processGate)
        {
            Process? process = _process;
            if (process is null || HasExited(process))
            {
                return null;
            }

            LocalCoreProcessIdentity identity = WindowsListenerOwnerTable.CaptureProcessIdentity(process.Id);
            return HasExited(process) ? null : identity;
        }
    }
    private void StartProcess(
        string executablePath,
        string configurationPath,
        string workingDirectory,
        string? safePaths)
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
        ApplySafePaths(startInfo, safePaths);
        Process process = _processStartInfoFactory(startInfo);
        process.EnableRaisingEvents = true;
        CancellationTokenSource lifetime = new CancellationTokenSource();
        process.Exited += ProcessExited;
        lock (_processGate)
        {
            _process = process;
            _lifetimeCts = lifetime;
            _instanceId = Guid.NewGuid();
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Unable to start Mihomo.");
            }

            Interlocked.Increment(ref _generation);

            ProcessJobObject? createdJob = null;
            try
            {
                if (ProcessJobObject.TryCreate(process, out createdJob))
                {
                    lock (_processGate)
                    {
                        if (ReferenceEquals(_process, process))
                        {
                            _processJob = createdJob;
                            createdJob = null;
                        }
                    }
                }
                else
                {
                    LogLineReceived?.Invoke("[ClashTray] 无法为 Mihomo 进程分配 Job Object，宿主被强制终止时核心可能残留。", true);
                }
            }
            finally
            {
                createdJob?.Dispose();
            }

            _ = DrainAsync(process.StandardOutput, isError: false, lifetime.Token);
            _ = DrainAsync(process.StandardError, isError: true, lifetime.Token);

            bool running = !HasExited(process);
            lock (_processGate)
            {
                if (ReferenceEquals(_process, process))
                {
                    State = running ? CoreState.Starting : CoreState.Failed;
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
            ProcessJobObject? failedJob = null;
            lock (_processGate)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                    _lifetimeCts = null;
                    _instanceId = Guid.Empty;
                    failedJob = _processJob;
                    _processJob = null;
                    State = CoreState.Failed;
                }
            }

            DisposeProcess(process, lifetime, failedJob);
            OnStateChanged();
            throw;
        }
    }

    private async Task<int> RunOneShotAsync(
        string executablePath,
        string arguments,
        string? workingDirectory,
        string? safePaths,
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
        ApplySafePaths(startInfo, safePaths);

        using Process process = _validationProcessFactory(startInfo);
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start Mihomo validation.");
        }

        using CancellationTokenSource drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task standardOutput = DrainValidationOutputAsync(process.StandardOutput, drainCancellation.Token);
        Task standardError = DrainValidationOutputAsync(process.StandardError, drainCancellation.Token);
        Task drains = Task.WhenAll(standardOutput, standardError);
        ProcessJobObject? job = null;
        using CancellationTokenSource cleanup = new();
        try
        {
            if (!ProcessJobObject.TryCreate(process, out job) && !process.HasExited)
            {
                throw new InvalidOperationException("无法为配置验证进程建立受管进程边界。");
            }

            await process.WaitForExitAsync(cancellationToken);
            await drains.WaitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (Exception failure)
        {
            cleanup.CancelAfter(_stopTimeout);
            job?.Dispose();
            job = null;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync(cleanup.Token);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                failure.Data["ValidationProcessCleanup"] = ErrorSanitizer.Sanitize(exception);
                LogLineReceived?.Invoke($"配置验证进程清理未确认：{ErrorSanitizer.Sanitize(exception)}", true);
            }

            await drainCancellation.CancelAsync();
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            try
            {
                await drains.WaitAsync(cleanup.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Cancellation/closing our redirected handles stops pipe readers. If they
                // finish after the cleanup deadline, still observe any terminal failure.
                _ = drains.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            throw;
        }
        finally
        {
            job?.Dispose();
        }
    }

    private static async Task DrainValidationOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        char[] buffer = new char[8 * 1024];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) > 0)
        {
        }
    }

    private static void ApplySafePaths(ProcessStartInfo startInfo, string? safePaths)
    {
        if (!string.IsNullOrWhiteSpace(safePaths))
        {
            startInfo.Environment["SAFE_PATHS"] = safePaths;
        }
    }

    private Task DrainAsync(StreamReader reader, bool isError, CancellationToken cancellationToken) =>
        DrainOutputAsync(
            reader,
            isError,
            (line, error) => LogLineReceived?.Invoke(line, error),
            cancellationToken);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "The drain loop is fire-and-forget; an unexpected failure must be observed and surfaced as a log line instead of faulting an unobserved task.")]
    internal static async Task DrainOutputAsync(
        TextReader reader,
        bool isError,
        Action<string, bool> logLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(logLine);
        BoundedOutputLineReader lineReader = new(reader);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await lineReader.ReadLineLimitedAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    logLine(line, isError);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logLine(
                $"[ClashTray] Mihomo 输出读取中断：{ErrorSanitizer.Sanitize(exception)}",
                true);
        }
    }

    private sealed class BoundedOutputLineReader(TextReader reader)
    {
        private const int ReadBufferCharacters = 1024;
        private readonly char[] _buffer = new char[ReadBufferCharacters];
        private int _bufferOffset;
        private int _bufferCount;
        private bool _endOfStream;

        public async Task<string?> ReadLineLimitedAsync(CancellationToken cancellationToken)
        {
            StringBuilder builder = new(Math.Min(MaxLogLineCharacters, 1024));
            bool truncated = false;
            while (true)
            {
                if (_bufferOffset >= _bufferCount && !_endOfStream)
                {
                    _bufferCount = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken);
                    _bufferOffset = 0;
                    _endOfStream = _bufferCount == 0;
                }

                if (_endOfStream)
                {
                    return builder.Length == 0 && !truncated
                        ? null
                        : FormatLogLine(builder, truncated);
                }

                char character = _buffer[_bufferOffset++];
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
            _instanceId = Guid.Empty;
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

    private void DisposeProcess(Process? process, CancellationTokenSource? lifetime, ProcessJobObject? job)
    {
        if (process is not null)
        {
            process.Exited -= ProcessExited;
            process.Dispose();
        }

        job?.Dispose();
        lifetime?.Dispose();
    }

    private void OnStateChanged()
    {
        lock (_stateEventGate)
        {
            StateChanged?.Invoke(this, State);
        }
    }
}
