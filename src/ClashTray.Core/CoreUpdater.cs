using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClashTray.Core;

public sealed record CoreUpdateManifest(string Version, Uri DownloadUri, string Sha256);

public sealed class CoreUpdater
{
    private const long MaxArchiveBytes = 128L * 1024 * 1024;
    private const long MaxExecutableBytes = 128L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly HttpClient _httpClient;
    private readonly AppPaths _paths;
    private readonly string? _managedUserSid;

    public CoreUpdater(
        AppPaths paths,
        HttpClient? httpClient = null,
        string? managedUserSid = null)
    {
        _paths = paths;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _managedUserSid = managedUserSid;
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

            await using (FileStream hashStream = File.OpenRead(archivePath))
            {
                string archiveHash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken));
                if (!archiveHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Mihomo core checksum validation failed.");
                }
            }

            using ZipArchive archive = await ZipFile.OpenReadAsync(archivePath, cancellationToken);
            ZipArchiveEntry[] coreEntries = archive.Entries
                .Where(entry => string.Equals(entry.Name, "mihomo.exe", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (coreEntries.Length != 1)
            {
                throw new InvalidDataException("The Mihomo archive must contain exactly one mihomo.exe.");
            }

            await ExtractEntryAsync(coreEntries[0], extractedCorePath, cancellationToken);
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

    public static void ValidateManifest(CoreUpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Uri? downloadUri = manifest.DownloadUri;
        string path = downloadUri?.AbsolutePath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(manifest.Version)
            || downloadUri is null
            || downloadUri.Scheme != Uri.UriSchemeHttps
            || !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !path.Contains("/MetaCubeX/mihomo/releases/download/", StringComparison.OrdinalIgnoreCase)
            || !path.Contains("windows-amd64", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(manifest.Sha256)
            || manifest.Sha256.Length != 64
            || manifest.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Mihomo update manifest is not an approved official release manifest.", nameof(manifest));
        }
    }

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
