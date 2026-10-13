namespace ClashTray.Core;

public static class CorePathPolicy
{
    public static bool IsManagedCorePath(AppPaths paths, string path)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            string fullPath = Path.GetFullPath(path);
            return PathEquals(fullPath, paths.ManagedCoreExecutable)
                && !HasReparsePointOnPath(fullPath, paths.ProgramRoot);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsManagedRuntimeDirectory(AppPaths paths, string path)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            string expected = Path.Combine(paths.RuntimeRoot, "mihomo");
            string fullPath = Path.GetFullPath(path);
            return PathEquals(fullPath, expected)
                && !HasReparsePointOnPath(fullPath, paths.ProgramRoot);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsManagedRuntimeFile(AppPaths paths, string path)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            return directory is not null
                && IsManagedRuntimeDirectory(paths, directory)
                && !HasReparsePointOnPath(fullPath, paths.ProgramRoot);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool HasReparsePointOnPath(string path, string stopAt)
    {
        string current = Path.GetFullPath(path);
        string boundary = Path.GetFullPath(stopAt);
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }

            if (PathEquals(current, boundary))
            {
                return false;
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent is null)
            {
                return true;
            }

            current = parent.FullName;
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}
