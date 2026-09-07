using System.IO.Compression;
using System.Security.Cryptography;
using System.Buffers.Binary;

namespace ClashTray.Core;

public sealed record CoreUpdateManifest(string Version, Uri DownloadUri, string Sha256);

public sealed class CoreUpdater
{
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
        var extractedPath = Path.Combine(stagingRoot, "extracted");
        var coreDirectory = Path.Combine(_paths.LocalRoot, "core");
        var targetPath = Path.Combine(coreDirectory, "mihomo.exe");
        var backupPath = targetPath + ".previous";

        try
        {
            using var response = await _httpClient.GetAsync(manifest.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(archivePath))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            await using (var hashStream = File.OpenRead(archivePath))
            {
                var archiveHash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken));
                if (!archiveHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Mihomo core checksum validation failed.");
                }
            }

            ZipFile.ExtractToDirectory(archivePath, extractedPath);
            var extractedCore = Directory.EnumerateFiles(extractedPath, "mihomo.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (extractedCore is null)
            {
                throw new InvalidDataException("The Mihomo archive did not contain mihomo.exe.");
            }

            ValidateWindowsAmd64Executable(extractedCore);

            Directory.CreateDirectory(coreDirectory);
            var candidatePath = targetPath + ".new";
            File.Copy(extractedCore, candidatePath, overwrite: true);
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
}
