using System.Diagnostics;
using System.Text.Json;

namespace ClashTray.Core;

internal sealed record LocalCoreProcessIdentity(
    int ProcessId,
    long StartTimeUtcTicks,
    string ExecutablePath);

internal sealed record LocalCoreShutdownJournalResult(
    bool Succeeded,
    bool RecordFound,
    string? Detail = null);

/// <summary>
/// Persists explicit-Quit ownership of a local Mihomo process so a later app
/// launch can safely finish a stop that the current process could not confirm.
/// </summary>
internal sealed class LocalCoreShutdownJournal
{
    private const int CurrentFormatVersion = 1;
    private const int MaximumRecordBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths;
    private readonly Func<string, bool> _managedCorePathValidator;

    public LocalCoreShutdownJournal(
        AppPaths paths,
        Func<string, bool>? managedCorePathValidator = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _managedCorePathValidator = managedCorePathValidator
            ?? (path => CorePathPolicy.IsManagedCorePath(paths, path));
    }

    public async Task<LocalCoreShutdownJournalResult> SavePendingStopAsync(
        LocalCoreProcessIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!IsValidIdentity(identity))
        {
            return new(false, false, "本地核心退出恢复记录中的进程身份无效或路径不属于受管核心。");
        }

        byte[] content = JsonSerializer.SerializeToUtf8Bytes(
            new JournalRecord(CurrentFormatVersion, identity),
            JsonOptions);
        if (content.Length > MaximumRecordBytes)
        {
            return new(false, false, "本地核心退出恢复记录超过大小上限。");
        }

        string temporaryPath = _paths.LocalCoreShutdownFile + ".tmp";
        bool ownsTemporaryFile = false;
        try
        {
            Directory.CreateDirectory(_paths.LocalRoot);
            if (IsReparsePoint(_paths.LocalCoreShutdownFile)
                || IsReparsePoint(temporaryPath))
            {
                return new(false, false, "本地核心退出恢复记录或暂存文件不能是重解析点。");
            }

            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                ownsTemporaryFile = true;
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _paths.LocalCoreShutdownFile, overwrite: true);
            ownsTemporaryFile = false;
            return new(true, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, false, ErrorSanitizer.Sanitize(exception));
        }
        finally
        {
            if (ownsTemporaryFile)
            {
                TryDeleteTemporary(temporaryPath);
            }
        }
    }

    public async Task<LocalCoreShutdownJournalResult> RecoverPendingStopAsync(
        Func<LocalCoreProcessIdentity, CancellationToken, Task<LocalCoreShutdownJournalResult>>? recoverProcessAsync = null,
        CancellationToken cancellationToken = default)
    {
        string journalPath = _paths.LocalCoreShutdownFile;
        string temporaryPath = journalPath + ".tmp";
        bool hasJournal = File.Exists(journalPath);
        bool hasTemporary = File.Exists(temporaryPath);
        if (!hasJournal && !hasTemporary)
        {
            return new(true, false);
        }

        if (hasJournal && hasTemporary)
        {
            return new(false, true, "本地核心退出恢复记录和暂存文件同时存在；状态有歧义，未操作任何进程。");
        }

        bool promoteTemporary = !hasJournal;
        string recordPath = promoteTemporary ? temporaryPath : journalPath;
        JournalRecord record;
        try
        {
            if (IsReparsePoint(recordPath))
            {
                return new(false, true, "本地核心退出恢复记录指向重解析点，未操作任何进程。");
            }

            FileInfo file = new(recordPath);
            if (file.Length is <= 0 or > MaximumRecordBytes)
            {
                return new(false, true, "本地核心退出恢复记录大小无效，未操作任何进程。");
            }

            byte[] content = await File.ReadAllBytesAsync(recordPath, cancellationToken).ConfigureAwait(false);
            record = JsonSerializer.Deserialize<JournalRecord>(content, JsonOptions)
                ?? throw new InvalidDataException("本地核心退出恢复记录为空。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new(false, true, ErrorSanitizer.Sanitize(exception));
        }

        if (record.Version != CurrentFormatVersion
            || record.Identity is not { } identity
            || !IsValidIdentity(identity))
        {
            return new(false, true, "本地核心退出恢复记录版本或进程身份无效；未操作任何进程。");
        }

        if (promoteTemporary)
        {
            try
            {
                File.Move(temporaryPath, journalPath, overwrite: false);
            }
            catch (IOException exception)
            {
                return new(false, true, ErrorSanitizer.Sanitize(exception));
            }
            catch (UnauthorizedAccessException exception)
            {
                return new(false, true, ErrorSanitizer.Sanitize(exception));
            }
        }

        Func<LocalCoreProcessIdentity, CancellationToken, Task<LocalCoreShutdownJournalResult>> recover =
            recoverProcessAsync ?? StopExactProcessAsync;
        LocalCoreShutdownJournalResult stopResult = await recover(identity, cancellationToken)
            .ConfigureAwait(false);
        if (!stopResult.Succeeded)
        {
            return stopResult with { RecordFound = true };
        }

        try
        {
            File.Delete(journalPath);
            return stopResult with { RecordFound = true };
        }
        catch (IOException exception)
        {
            return new(false, true, ErrorSanitizer.Sanitize(exception));
        }
        catch (UnauthorizedAccessException exception)
        {
            return new(false, true, ErrorSanitizer.Sanitize(exception));
        }
    }

    public Task ClearPendingStopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string journalPath = _paths.LocalCoreShutdownFile;
        string temporaryPath = journalPath + ".tmp";
        if (IsReparsePoint(journalPath) || IsReparsePoint(temporaryPath))
        {
            throw new InvalidDataException("本地核心退出恢复记录不能是重解析点。");
        }

        if (File.Exists(journalPath))
        {
            File.Delete(journalPath);
        }

        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        return Task.CompletedTask;
    }

    private static async Task<LocalCoreShutdownJournalResult> StopExactProcessAsync(
        LocalCoreProcessIdentity identity,
        CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(identity.ProcessId);
        }
        catch (ArgumentException)
        {
            return new(true, true, "原本地核心进程已退出。");
        }

        using (process)
        {
            try
            {
                if (process.HasExited)
                {
                    return new(true, true, "原本地核心进程已退出。");
                }

                string? executablePath = process.MainModule?.FileName;
                long startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                if (!string.Equals(
                        Path.GetFullPath(executablePath ?? string.Empty),
                        identity.ExecutablePath,
                        StringComparison.OrdinalIgnoreCase)
                    || startTimeUtcTicks != identity.StartTimeUtcTicks)
                {
                    return new(true, true, "进程身份已变化；保留当前进程并清除陈旧恢复记录。");
                }

                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                    .ConfigureAwait(false);
                return new(true, true, "已停止与恢复记录身份完全匹配的本地核心进程。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or UnauthorizedAccessException
                    or TimeoutException
                    or IOException)
            {
                if (process.HasExited)
                {
                    return new(true, true, "原本地核心进程已退出。");
                }

                return new(false, true, ErrorSanitizer.Sanitize(exception));
            }
        }
    }

    private bool IsValidIdentity(LocalCoreProcessIdentity identity)
    {
        if (identity.ProcessId <= 0
            || identity.StartTimeUtcTicks <= DateTime.MinValue.Ticks
            || identity.StartTimeUtcTicks >= DateTime.MaxValue.Ticks
            || string.IsNullOrWhiteSpace(identity.ExecutablePath)
            || !Path.IsPathFullyQualified(identity.ExecutablePath))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(identity.ExecutablePath);
            return string.Equals(fullPath, identity.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                && _managedCorePathValidator(fullPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path) =>
        File.Exists(path)
        && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record JournalRecord(int Version, LocalCoreProcessIdentity? Identity);
}
