using System.Text;

namespace ClashTray.Core;

internal static class ServiceMessageReader
{
    public static async Task<string?> ReadLineAsync(StreamReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        StringBuilder builder = new();
        char[] buffer = new char[1024];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) { return builder.Length == 0 ? null : builder.ToString().TrimEnd('\r'); }
            for (int index = 0; index < count; index++)
            {
                if (buffer[index] == '\n') { return builder.ToString().TrimEnd('\r'); }
                if (builder.Length >= maximumCharacters) { throw new InvalidDataException("Service message exceeded the size limit."); }
                builder.Append(buffer[index]);
            }
        }
    }
}
