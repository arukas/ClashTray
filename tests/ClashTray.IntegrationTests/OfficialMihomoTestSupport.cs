namespace ClashTray.IntegrationTests;

internal static class OfficialMihomoTestSupport
{
    public static string? FindMihomoExecutable()
    {
        bool required = string.Equals(
            Environment.GetEnvironmentVariable("CLASHTRAY_MIHOMO_REQUIRED"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        string? configured = Environment.GetEnvironmentVariable("CLASHTRAY_MIHOMO_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }

            if (required)
            {
                throw new InvalidOperationException(
                    "CLASHTRAY_MIHOMO_REQUIRED is true, but the explicitly supplied Mihomo executable does not exist.");
            }
        }
        else if (required)
        {
            throw new InvalidOperationException(
                "CLASHTRAY_MIHOMO_REQUIRED is true, but CLASHTRAY_MIHOMO_PATH was not supplied.");
        }

        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "packaging",
                "out",
                "payload-local-full",
                "Core",
                "mihomo.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
