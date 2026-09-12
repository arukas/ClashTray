using System.Diagnostics;

namespace ClashTray.Core;

public sealed class CoreDiscovery
{
    private readonly AppPaths _paths;

    public CoreDiscovery(AppPaths paths)
    {
        _paths = paths;
    }

    public string ManagedExecutablePath => _paths.ManagedCoreExecutable;

    public string? FindExecutable(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)
            && CorePathPolicy.IsManagedCorePath(_paths, configuredPath)
            && File.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return CorePathPolicy.IsManagedCorePath(_paths, _paths.ManagedCoreExecutable)
            && File.Exists(_paths.ManagedCoreExecutable)
            && File.Exists(_paths.ManagedCoreMetadata)
            ? _paths.ManagedCoreExecutable
            : null;
    }

    public static string? GetVersion(string executablePath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(executablePath).FileVersion;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
