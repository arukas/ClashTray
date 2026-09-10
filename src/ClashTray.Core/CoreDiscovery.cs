using System.Diagnostics;

namespace ClashTray.Core;

public sealed class CoreDiscovery
{
    private readonly AppPaths _paths;

    public CoreDiscovery(AppPaths paths)
    {
        _paths = paths;
    }

    public string? FindExecutable(string? configuredPath = null)
    {
        List<string> candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(configuredPath);
        }

        candidates.Add(Path.Combine(_paths.LocalRoot, "core", "mihomo.exe"));
        candidates.Add(Path.Combine(_paths.ProgramRoot, "core", "mihomo.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "mihomo.exe"));

        return candidates
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .FirstOrDefault();
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
    }
}
