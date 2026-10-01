using System.Diagnostics;

namespace ClashTray.Core.Tests;

internal static class TestDirectoryCleanup
{
    internal static async Task DeleteAsync(string path, Action? sharingViolationObserved = null)
    {
        string resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ClashTrayTests")));
        if (!string.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
        {
            throw new InvalidOperationException("Cleanup is restricted to a direct GUID test fixture directory.");
        }

        long start = Stopwatch.GetTimestamp();
        while (Directory.Exists(resolved))
        {
            if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("A test fixture cleanup root must not be a reparse point.");
            }

            try
            {
                Directory.Delete(resolved, recursive: true);
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) == 32 && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(2))
            {
                sharingViolationObserved?.Invoke();
                await Task.Delay(TimeSpan.FromMilliseconds(40));
            }
        }
    }
}
