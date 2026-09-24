using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;

namespace ClashTray.Core;

/// <summary>
/// Writes sanitized exception diagnostics synchronously with bounded records
/// and bounded local retention. Failure is deliberately contained at this boundary.
/// </summary>
public static class BoundedDiagnosticWriter
{
    public const int MaximumFileCount = 3;
    public const int MaximumFileBytes = 1024 * 1024;
    public const int MaximumRecordBytes = 16 * 1024;
    private const int MaximumExceptionDetails = 8;
    private const int MaximumStackFrames = 6;
    private const int MaximumTypeCharacters = 160;
    private const int MaximumMessageCharacters = 512;
    private const int MaximumStackFrameCharacters = 256;
    private const int MaximumVersionCharacters = 64;
    private const int MaximumDiagnosticTextBytes = MaximumRecordBytes - 4;
    private const string FileStem = "startup-error-";
    private static readonly object WriteGate = new();

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "This is the final unhandled-exception diagnostic boundary; filesystem and sanitizer failures must not escape and recurse into crash handling.")]
    public static bool TryWriteException(string? directoryPath, Exception? exception)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || exception is null)
            {
                return false;
            }

            string fullDirectoryPath = Path.GetFullPath(directoryPath);
            byte[] record = CreateRecordBytes(exception, DateTimeOffset.UtcNow);
            lock (WriteGate)
            {
                Directory.CreateDirectory(fullDirectoryPath);
                string[] filePaths = Enumerable.Range(0, MaximumFileCount)
                    .Select(index => Path.Combine(fullDirectoryPath, $"{FileStem}{index}.log"))
                    .ToArray();
                RemoveOversizedFiles(filePaths);
                RemoveIncompleteTrailingRecord(filePaths[0]);
                if (File.Exists(filePaths[0])
                    && new FileInfo(filePaths[0]).Length + record.Length > MaximumFileBytes)
                {
                    RotateFiles(filePaths);
                }

                using FileStream stream = new(
                    filePaths[0],
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.WriteThrough);
                stream.Write(record);
                stream.Flush(flushToDisk: true);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static byte[] CreateRecordBytes(Exception exception, DateTimeOffset timestamp)
    {
        StringBuilder record = new();
        int formattedBytes = 0;
        bool truncated = false;
        AppendBudgeted(
            record,
            timestamp.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ref formattedBytes,
            ref truncated);
        AppendBudgeted(
            record,
            $" app-version={GetApplicationVersion()} unhandled exception: ",
            ref formattedBytes,
            ref truncated);

        Queue<Exception> pending = new();
        HashSet<Exception> visited = new(ReferenceEqualityComparer.Instance);
        pending.Enqueue(exception);
        int detailCount = 0;
        while (pending.Count > 0
            && detailCount < MaximumExceptionDetails
            && formattedBytes < MaximumDiagnosticTextBytes)
        {
            Exception current = pending.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            if (detailCount > 0)
            {
                AppendBudgeted(record, " | inner: ", ref formattedBytes, ref truncated);
            }

            string type = current.GetType().FullName ?? current.GetType().Name;
            AppendBudgeted(
                record,
                ToSingleLine(ErrorSanitizer.Sanitize(LimitCharacters(type, MaximumTypeCharacters))),
                ref formattedBytes,
                ref truncated);
            AppendBudgeted(record, ": ", ref formattedBytes, ref truncated);
            string message = LimitCharacters(current.Message, MaximumMessageCharacters);
            AppendBudgeted(
                record,
                ToSingleLine(ErrorSanitizer.Sanitize(message)),
                ref formattedBytes,
                ref truncated);
            detailCount++;

            AppendStackFrames(current, record, ref formattedBytes, ref truncated);
            if (current is AggregateException aggregate)
            {
                int remainingSlots = Math.Max(0, MaximumExceptionDetails - detailCount - pending.Count);
                int childrenToQueue = Math.Min(aggregate.InnerExceptions.Count, remainingSlots);
                for (int index = 0; index < childrenToQueue; index++)
                {
                    pending.Enqueue(aggregate.InnerExceptions[index]);
                }

                if (childrenToQueue < aggregate.InnerExceptions.Count)
                {
                    truncated = true;
                }
            }
            else if (current.InnerException is Exception inner)
            {
                if (pending.Count < MaximumExceptionDetails - detailCount)
                {
                    pending.Enqueue(inner);
                }
                else
                {
                    truncated = true;
                }
            }
        }

        if (pending.Count > 0 || truncated)
        {
            record.Append("...");
        }

        return EncodeBoundedRecord(record.ToString());
    }

    private static void AppendStackFrames(
        Exception exception,
        StringBuilder record,
        ref int formattedBytes,
        ref bool truncated)
    {
        StackTrace stack = new(exception, fNeedFileInfo: false);
        for (int index = 0;
            index < MaximumStackFrames && formattedBytes < MaximumDiagnosticTextBytes;
            index++)
        {
            StackFrame? frame = stack.GetFrame(index);
            MethodBase? method = frame?.GetMethod();
            if (method is null)
            {
                break;
            }

            string declaringType = method.DeclaringType?.FullName ?? method.Module.Name;
            string location = LimitCharacters(
                $"{declaringType}.{method.Name}",
                MaximumStackFrameCharacters);
            AppendBudgeted(record, index == 0 ? " | at " : " <- ", ref formattedBytes, ref truncated);
            AppendBudgeted(
                record,
                ToSingleLine(ErrorSanitizer.Sanitize(location)),
                ref formattedBytes,
                ref truncated);
        }
    }

    private static string GetApplicationVersion()
    {
        string version = typeof(BoundedDiagnosticWriter).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? typeof(BoundedDiagnosticWriter).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        return LimitCharacters(version, MaximumVersionCharacters);
    }

    private static string LimitCharacters(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..maximumCharacters];

    private static void AppendBudgeted(
        StringBuilder record,
        string value,
        ref int formattedBytes,
        ref bool truncated)
    {
        foreach (Rune rune in value.EnumerateRunes())
        {
            int runeBytes = rune.Utf8SequenceLength;
            if (formattedBytes + runeBytes > MaximumDiagnosticTextBytes)
            {
                truncated = true;
                return;
            }

            record.Append(rune.ToString());
            formattedBytes += runeBytes;
        }
    }

    private static string ToSingleLine(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal);

    private static byte[] EncodeBoundedRecord(string value)
    {
        using MemoryStream buffer = new();
        byte[] runeBuffer = new byte[4];
        bool truncated = false;
        foreach (Rune rune in value.EnumerateRunes())
        {
            int count = rune.EncodeToUtf8(runeBuffer);
            if (buffer.Length + count + 4 > MaximumRecordBytes - 1)
            {
                truncated = true;
                break;
            }

            buffer.Write(runeBuffer, 0, count);
        }

        if (truncated)
        {
            buffer.Write("..."u8);
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static void RemoveIncompleteTrailingRecord(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length == 0)
        {
            return;
        }

        stream.Position = stream.Length - 1;
        if (stream.ReadByte() == (byte)'\n')
        {
            return;
        }

        byte[] buffer = new byte[4096];
        long remaining = stream.Length;
        while (remaining > 0)
        {
            int count = (int)Math.Min(buffer.Length, remaining);
            remaining -= count;
            stream.Position = remaining;
            int read = stream.Read(buffer, 0, count);
            for (int index = read - 1; index >= 0; index--)
            {
                if (buffer[index] == (byte)'\n')
                {
                    stream.SetLength(remaining + index + 1);
                    return;
                }
            }
        }

        stream.SetLength(0);
    }
    private static void RemoveOversizedFiles(string[] filePaths)
    {
        foreach (string path in filePaths)
        {
            if (File.Exists(path) && new FileInfo(path).Length > MaximumFileBytes)
            {
                File.Delete(path);
            }
        }
    }

    private static void RotateFiles(string[] filePaths)
    {
        if (File.Exists(filePaths[MaximumFileCount - 1]))
        {
            File.Delete(filePaths[MaximumFileCount - 1]);
        }

        for (int index = MaximumFileCount - 2; index >= 0; index--)
        {
            if (File.Exists(filePaths[index]))
            {
                File.Move(filePaths[index], filePaths[index + 1], overwrite: true);
            }
        }
    }
}