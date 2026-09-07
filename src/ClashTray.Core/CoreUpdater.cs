using System.IO.Compression;
using System.Security.Cryptography;
using System.Buffers.Binary;

namespace ClashTray.Core;

public sealed record CoreUpdateManifest(string Version, Uri DownloadUri, string Sha256);

public sealed class CoreUpdater
{
    private const long MaxArchiveBytes = 128L * 1024 * 1024;
    private const long MaxExecutableBytes = 128L * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly AppPaths _paths;

    public CoreUpdater(AppPaths paths, HttpClient? httpClient = null)
    {
        _paths = paths;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<string> DownloadAndInstallAsync(CoreUpdateManifest manifest, CancellationToken cancellationToken = default)
    {
        ValidateManifest(manifest);
        _paths.EnsureDirectories();
        var stagingRoot = Path.Combine(_paths.LocalRoot, "core-update", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        var archivePath = Path.Combine(stagingRoot, "mihomo.zip");
        var extractedCorePath = Path.Combine(stagingRoot, "mihomo.exe");
        var coreDirectory = Path.Combine(_paths.LocalRoot, "core");
        var targetPath = Path.Combine(coreDirectory, "mihomo.exe");
        var backupPath = targetPath + ".previous";

        try
        {
            using var response = await _httpClient.GetAsync(manifest.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await DownloadToFileAsync(response.Content, archivePath, cancellationToken);

            await using (var hashStream = File.OpenRead(archivePath))
            {
                var archiveHash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken));
                if (!archiveHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Mihomo core checksum validation failed.");
                }
            }

            using var archive = ZipFile.OpenRead(archivePath);
            var coreEntries = archive.Entries
                .Where(entry => string.Equals(entry.Name, "mihomo.exe", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (coreEntries.Length != 1)
            {
                throw new InvalidDataException("The Mihomo archive must contain exactly one mihomo.exe.");
            }

            await ExtractEntryAsync(coreEntries[0], extractedCorePath, cancellationToken);
            ValidateWindowsAmd64Executable(extractedCorePath);

            Directory.CreateDirectory(coreDirectory);
            var candidatePath = targetPath + ".new";
            File.Copy(extractedCorePath, candidatePath, overwrite: true);
            if (File.Exists(targetPath))
            {
                File.Replace(candidatePath, targetPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(candidatePath, targetPath);
            }

            return targetPath;
        }
        catch
        {
            var candidatePath = targetPath + ".new";
            if (File.Exists(candidatePath))
            {
                File.Delete(candidatePath);
            }

            if (!File.Exists(targetPath) && File.Exists(backupPath))
            {
                File.Move(backupPath, targetPath);
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    private static void ValidateManifest(CoreUpdateManifest manifest)
    {
        var path = manifest.DownloadUri.AbsolutePath;
        if (string.IsNullOrWhiteSpace(manifest.Version)
            || manifest.DownloadUri.Scheme != Uri.UriSchemeHttps
            || !manifest.DownloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !path.Contains("/MetaCubeX/mihomo/releases/download/", StringComparison.OrdinalIgnoreCase)
            || !path.Contains("windows-amd64", StringComparison.OrdinalIgnoreCase)
            || manifest.Sha256.Length != 64
            || manifest.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Mihomo update manifest is not an approved official release manifest.", nameof(manifest));
        }
    }

    private static void ValidateWindowsAmd64Executable(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 0x40)
        {
            throw new InvalidDataException("The Mihomo executable is too small to be a Windows PE file.");
        }

        if (stream.Length > MaxExecutableBytes)
        {
            throw new InvalidDataException("The Mihomo executable exceeds the maximum allowed size.");
        }

        Span<byte> dosHeader = stackalloc byte[0x40];
        stream.ReadExactly(dosHeader);
        if (dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
        {
            throw new InvalidDataException("The Mihomo archive did not contain a Windows executable.");
        }

        var peHeaderOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader[0x3C..]);
        if (peHeaderOffset < 0 || peHeaderOffset > stream.Length - 6)
        {
            throw new InvalidDataException("The Mihomo executable has an invalid PE header.");
        }

        stream.Position = peHeaderOffset;
        Span<byte> peHeader = stackalloc byte[6];
        stream.ReadExactly(peHeader);
        if (peHeader[0] != (byte)'P'
            || peHeader[1] != (byte)'E'
            || peHeader[2] != 0
            || peHeader[3] != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(peHeader[4..]) != 0x8664)
        {
            throw new InvalidDataException("The Mihomo executable is not a Windows x64 binary.");
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

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous);
        var buffer = new byte[64 * 1024];
        long totalBytes = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
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

        await using var input = entry.Open();
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous);
        var buffer = new byte[64 * 1024];
        long totalBytes = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
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
}
