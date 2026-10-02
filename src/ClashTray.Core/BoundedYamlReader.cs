using System.Text;

namespace ClashTray.Core;

// A FileInfo pre-check is not a resource bound: files can grow while read.
// Count actual bytes before decoding, then bound lines and each decoded line.
internal static class BoundedYamlReader
{
    public static async Task<string[]> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadAsync(file, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string[]> ReadAsync(Stream source, CancellationToken cancellationToken)
    {
        using MemoryStream bounded = new();
        byte[] bytes = new byte[65536];
        int total = 0;
        while (true)
        {
            int read = await source.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, RuntimeConfigBuilder.MaximumInputBytes - total + 1)), cancellationToken).ConfigureAwait(false);
            if (read == 0) { break; }
            total += read;
            if (total > RuntimeConfigBuilder.MaximumInputBytes) { throw new InvalidDataException($"Configuration exceeds the {RuntimeConfigBuilder.MaximumInputBytes}-byte runtime builder limit."); }
            await bounded.WriteAsync(bytes.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        bounded.Position = 0;
        // Preserve ReadAllLines' BOM detection and encoding compatibility.
        using StreamReader reader = new(bounded, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        char[] characters = new char[4096];
        StringBuilder line = new();
        List<string> lines = [];
        bool carriageReturn = false;
        while (true)
        {
            int read = await reader.ReadAsync(characters.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) { break; }
            for (int index = 0; index < read; index++)
            {
                char current = characters[index];
                if (current == '\n' && carriageReturn) { carriageReturn = false; continue; }
                carriageReturn = current == '\r';
                if (current is '\r' or '\n') { AddLine(); }
                else
                {
                    if (line.Length >= RuntimeConfigBuilder.MaximumLineCharacters) { throw new InvalidDataException($"Configuration line {lines.Count + 1} exceeds the {RuntimeConfigBuilder.MaximumLineCharacters}-character runtime builder limit."); }
                    line.Append(current);
                }
            }
        }
        if (line.Length > 0) { AddLine(); }
        return lines.ToArray();

        void AddLine()
        {
            if (lines.Count >= RuntimeConfigBuilder.MaximumInputLines) { throw new InvalidDataException($"Configuration exceeds the {RuntimeConfigBuilder.MaximumInputLines}-line runtime builder limit."); }
            lines.Add(line.ToString());
            line.Clear();
        }
    }
}
