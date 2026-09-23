using System.Diagnostics.CodeAnalysis;
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
        record.Append(timestamp.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        record.Append(" unhandled exception: ");
        Queue<Exception> pending = new();
        HashSet<Exception> visited = new(ReferenceEqualityComparer.Instance);
        pending.Enqueue(exception);
        int detailCount = 0;
        while (pending.Count > 0 && detailCount < MaximumExceptionDetails)
        {
            Exception current = pending.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            if (detailCount > 0)
            {
                record.Append(" | inner: ");
            }

            string type = current.GetType().FullName ?? current.GetType().Name;
            record.Append(ToSingleLine(ErrorSanitizer.Sanitize(type)));
            record.Append(": ");
            record.Append(ToSingleLine(ErrorSanitizer.Sanitize(current.Message)));
            detailCount++;

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    pending.Enqueue(inner);
                }
            }
            else if (current.InnerException is Exception inner)
            {
                pending.Enqueue(inner);
            }
        }

        return EncodeBoundedRecord(record.ToString());
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