using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClashTray.Core;

public sealed record CoreUpdateManifest(string Version, Uri DownloadUri, string Sha256);

public sealed class CoreUpdater : IDisposable
{
    private const long MaxArchiveBytes = 128L * 1024 * 1024;
    private const string OfficialArchiveExecutableName = "mihomo-windows-amd64.exe";
    private const long MaxExecutableBytes = 128L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly AppPaths _paths;
    private readonly string? _managedUserSid;

    public CoreUpdater(
        AppPaths paths,
        HttpClient? httpClient = null,
        string? managedUserSid = null)
    {
        _paths = paths;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _managedUserSid = managedUserSid;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    public async Task<string> DownloadAndInstallAsync(
        CoreUpdateManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateManifest(manifest);
        _paths.EnsureProgramDataDirectories(_managedUserSid);

        string stagingRoot = Path.Combine(_paths.ProgramRoot, "core-update", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        string archivePath = Path.Combine(stagingRoot, "mihomo.zip");
        string extractedCorePath = Path.Combine(stagingRoot, "mihomo.exe");
        string coreDirectory = _paths.CoreRoot;
        string targetPath = _paths.ManagedCoreExecutable;
        string metadataPath = _paths.ManagedCoreMetadata;
        string candidatePath = targetPath + ".new";
        string candidateMetadataPath = metadataPath + ".new";
        string backupPath = targetPath + ".previous";
        string backupMetadataPath = metadataPath + ".previous";
        bool coreReplaced = false;
        bool metadataReplaced = false;
        bool coreExisted = File.Exists(targetPath);
        bool metadataExisted = File.Exists(metadataPath);

        try
        {
            if (coreExisted || metadataExisted)
            {
                if (!coreExisted || !metadataExisted)
                {
                    throw new InvalidDataException("现有 Mihomo 核心不是 ClashTray 管理的核心，服务不会覆盖它。");
                }

                await ManagedCoreVerifier.ValidateAsync(_paths, cancellationToken);
            }

            using HttpResponseMessage response = await _httpClient.GetAsync(
                manifest.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await DownloadToFileAsync(response.Content, archivePath, cancellationToken);

            if (!string.IsNullOrEmpty(manifest.Sha256))
            {
                await using FileStream hashStream = File.OpenRead(archivePath);
                string archiveHash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken));
                if (!archiveHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Mihomo core checksum validation failed.");
                }
            }

            using ZipArchive archive = await ZipFile.OpenReadAsync(archivePath, cancellationToken);
            ZipArchiveEntry[] namedCoreEntries = archive.Entries
                .Where(entry => string.Equals(entry.Name, OfficialArchiveExecutableName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (namedCoreEntries.Length != 1
                || !namedCoreEntries[0].FullName.Equals(OfficialArchiveExecutableName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The Mihomo archive must contain exactly one root {OfficialArchiveExecutableName} entry.");
            }

            await ExtractEntryAsync(namedCoreEntries[0], extractedCorePath, cancellationToken);
            ManagedCoreVerifier.ValidateWindowsAmd64Executable(extractedCorePath);
            string executableHash;
            await using (FileStream executable = File.OpenRead(extractedCorePath))
            {
                executableHash = Convert.ToHexString(await SHA256.HashDataAsync(executable, cancellationToken));
            }

            ManagedCoreMetadata metadata = new(
                manifest.Version,
                manifest.DownloadUri,
                manifest.Sha256,
                executableHash);
            await File.WriteAllTextAsync(
                candidateMetadataPath,
                JsonSerializer.Serialize(metadata, JsonOptions),
                cancellationToken);
            File.Copy(extractedCorePath, candidatePath, overwrite: true);

            ReplaceWithBackup(candidatePath, targetPath, backupPath);
            coreReplaced = true;
            ReplaceWithBackup(candidateMetadataPath, metadataPath, backupMetadataPath);
            metadataReplaced = true;

            if (!string.IsNullOrWhiteSpace(_managedUserSid))
            {
                WindowsPathSecurity.ProtectManagedCoreDirectory(coreDirectory, _managedUserSid);
                WindowsPathSecurity.ProtectManagedCoreFile(targetPath, _managedUserSid);
                WindowsPathSecurity.ProtectManagedCoreFile(metadataPath, _managedUserSid);
            }

            await ManagedCoreVerifier.ValidateAsync(_paths, cancellationToken);
            return targetPath;
        }
        catch
        {
            TryDelete(candidatePath);
            TryDelete(candidateMetadataPath);

            if (coreReplaced)
            {
                RestoreReplacedFile(targetPath, backupPath, coreExisted);
            }

            if (metadataReplaced)
            {
                RestoreReplacedFile(metadataPath, backupMetadataPath, metadataExisted);
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    public async Task<string> RollbackLastInstallAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureProgramDataDirectories(_managedUserSid);

        string targetPath = _paths.ManagedCoreExecutable;
        string metadataPath = _paths.ManagedCoreMetadata;
        string backupPath = targetPath + ".previous";
        string backupMetadataPath = metadataPath + ".previous";
        if (!File.Exists(targetPath)
            || !File.Exists(metadataPath)
            || !File.Exists(backupPath)
            || !File.Exists(backupMetadataPath))
        {
            throw new InvalidOperationException("没有可用的 Mihomo 核心回滚版本。");
        }

        await ManagedCoreVerifier.ValidateFilesAsync(
            _paths,
            backupPath,
            backupMetadataPath,
            cancellationToken: cancellationToken);

        string displacedPath = targetPath + ".failed";
        string displacedMetadataPath = metadataPath + ".failed";
        bool coreSwapped = false;
        bool metadataSwapped = false;
        try
        {
            ReplaceFromBackup(backupPath, targetPath, displacedPath);
            coreSwapped = true;
            ReplaceFromBackup(backupMetadataPath, metadataPath, displacedMetadataPath);
            metadataSwapped = true;
            await ManagedCoreVerifier.ValidateAsync(_paths, cancellationToken);

            if (!string.IsNullOrWhiteSpace(_managedUserSid))
            {
                WindowsPathSecurity.ProtectManagedCoreFile(targetPath, _managedUserSid);
                WindowsPathSecurity.ProtectManagedCoreFile(metadataPath, _managedUserSid);
            }

            TryDelete(displacedPath);
            TryDelete(displacedMetadataPath);
            return targetPath;
        }
        catch
        {
            if (metadataSwapped)
            {
                RestoreSwappedFile(metadataPath, backupMetadataPath, displacedMetadataPath);
            }

            if (coreSwapped)
            {
                RestoreSwappedFile(targetPath, backupPath, displacedPath);
            }

            throw;
        }
    }

    public static void ValidateManifest(CoreUpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Uri? downloadUri = manifest.DownloadUri;
        string version = manifest.Version ?? string.Empty;
        bool checksumValid = string.IsNullOrEmpty(manifest.Sha256)
            || (manifest.Sha256.Length == 64 && manifest.Sha256.All(IsAsciiHexDigit));
        if (downloadUri is null
            || !IsSupportedVersionTag(version)
            || !IsCanonicalOfficialReleaseUri(downloadUri, version)
            || !checksumValid)
        {
            throw new ArgumentException("Mihomo update manifest is not an approved official release manifest.", nameof(manifest));
        }
    }

    private static bool IsCanonicalOfficialReleaseUri(Uri downloadUri, string version)
    {
        if (!downloadUri.IsAbsoluteUri
            || !downloadUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || downloadUri.Port != 443
            || !downloadUri.IsDefaultPort
            || downloadUri.UserInfo.Length != 0
            || downloadUri.Query.Length != 0
            || downloadUri.Fragment.Length != 0)
        {
            return false;
        }

        string original = downloadUri.OriginalString;
        int authorityStart = original.IndexOf("://", StringComparison.Ordinal);
        int pathStart = authorityStart < 0 ? -1 : original.IndexOf('/', authorityStart + 3);
        if (authorityStart != Uri.UriSchemeHttps.Length
            || !original.AsSpan(0, authorityStart).Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || pathStart < 0
            || !original.AsSpan(authorityStart + 3, pathStart - authorityStart - 3)
                .Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string rawPath = original[(pathStart + 1)..];
        if (rawPath.IndexOfAny(['%', '\\', '?', '#']) >= 0)
        {
            return false;
        }

        string[] segments = rawPath.Split('/');
        if (segments.Length != 6
            || segments.Any(string.IsNullOrEmpty)
            || segments[0] != "MetaCubeX"
            || segments[1] != "mihomo"
            || segments[2] != "releases"
            || segments[3] != "download"
            || !segments[4].Equals(version, StringComparison.Ordinal)
            || !segments[5].Equals($"mihomo-windows-amd64-{version}.zip", StringComparison.Ordinal))
        {
            return false;
        }

        return downloadUri.AbsolutePath.Equals($"/{rawPath}", StringComparison.Ordinal);
    }

    private static bool IsSupportedVersionTag(string version) =>
        version.Length is > 0 and <= 128
        && Regex.IsMatch(
            version,
            @"\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?\z",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(100));

    private static bool IsAsciiHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static void ReplaceWithBackup(string candidatePath, string targetPath, string backupPath)
    {
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        if (File.Exists(targetPath))
        {
            File.Replace(candidatePath, targetPath, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(candidatePath, targetPath);
        }
    }

    private static void ReplaceFromBackup(string backupPath, string targetPath, string displacedPath)
    {
        TryDelete(displacedPath);
        if (File.Exists(targetPath))
        {
            File.Replace(backupPath, targetPath, displacedPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(backupPath, targetPath);
        }
    }

    private static void RestoreSwappedFile(string targetPath, string backupPath, string displacedPath)
    {
        if (!File.Exists(displacedPath))
        {
            return;
        }

        TryDelete(backupPath);
        if (File.Exists(targetPath))
        {
            File.Replace(displacedPath, targetPath, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(displacedPath, targetPath);
        }
    }

    private static void RestoreReplacedFile(string targetPath, string backupPath, bool targetExisted)
    {
        if (File.Exists(backupPath))
        {
            TryDelete(targetPath);
            File.Move(backupPath, targetPath);
        }
        else if (!targetExisted)
        {
            TryDelete(targetPath);
        }
    }

    private static async Task DownloadToFileAsync(
        HttpContent content,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxArchiveBytes)
        {
            throw new InvalidDataException("The Mihomo archive exceeds the maximum allowed size.");
        }

        await using Stream input = await content.ReadAsStreamAsync(cancellationToken);
        await using FileStream output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous);
        byte[] buffer = new byte[64 * 1024];
        long totalBytes = 0;
        while (true)
        {
            int count = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
            {
                return;
            }

            if (totalBytes > MaxArchiveBytes - count)
            {
                throw new InvalidDataException("The Mihomo archive exceeds the maximum allowed size.");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            totalBytes += count;
        }
    }

    private static async Task ExtractEntryAsync(
        ZipArchiveEntry entry,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (entry.Length <= 0 || entry.Length > MaxExecutableBytes)
        {
            throw new InvalidDataException("The Mihomo executable entry exceeds the maximum allowed size.");
        }

        await using Stream input = await entry.OpenAsync(cancellationToken);
        await using FileStream output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous);
        byte[] buffer = new byte[64 * 1024];
        long totalBytes = 0;
        while (true)
        {
            int count = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
            {
                return;
            }

            if (totalBytes > MaxExecutableBytes - count)
            {
                throw new InvalidDataException("The Mihomo executable entry exceeds the maximum allowed size.");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            totalBytes += count;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
