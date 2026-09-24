using System.Security.Cryptography;

namespace ClashTray.IntegrationTests;

internal static class OfficialMihomoTestSupport
{
    private const long MaximumPinnedArchiveBytes = 128L * 1024 * 1024;
    internal const string PinnedArchiveSha256 = "38b2420799d9e7cde77ec1a19c7150dd17ca77f7fb82d9f62cb8763a307eee67";

    public static string? FindVerifiedMihomoArchive()
    {
        bool required = string.Equals(
            Environment.GetEnvironmentVariable("CLASHTRAY_MIHOMO_REQUIRED"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        string? configured = Environment.GetEnvironmentVariable("CLASHTRAY_MIHOMO_ARCHIVE_PATH");
        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured))
        {
            if (required)
            {
                throw new InvalidOperationException(
                    "CLASHTRAY_MIHOMO_REQUIRED is true, but a verified CLASHTRAY_MIHOMO_ARCHIVE_PATH was not supplied.");
            }

            return null;
        }

        string fullPath = Path.GetFullPath(configured);
        if (new FileInfo(fullPath).Length > MaximumPinnedArchiveBytes)
        {
            throw new InvalidDataException("The supplied official Mihomo archive exceeds the controlled archive size limit.");
        }

        using FileStream stream = File.OpenRead(fullPath);
        string actualSha256 = Convert.ToHexString(SHA256.HashData(stream));
        if (!actualSha256.Equals(PinnedArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The supplied official Mihomo archive does not match its pinned SHA-256.");
        }

        return fullPath;
    }
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
