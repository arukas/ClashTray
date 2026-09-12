using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClashTray.Core;

public sealed record ManagedCoreMetadata(
    string Version,
    Uri DownloadUri,
    string ArchiveSha256,
    string ExecutableSha256);

public static class ManagedCoreVerifier
{
    private const long MaxExecutableBytes = 128L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Task ValidateAsync(
        AppPaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return ValidateFilesAsync(
            paths,
            paths.ManagedCoreExecutable,
            paths.ManagedCoreMetadata,
            requireExactManagedPaths: true,
            cancellationToken);
    }

    internal static async Task ValidateFilesAsync(
        AppPaths paths,
        string executablePath,
        string metadataPath,
        bool requireExactManagedPaths = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!IsCoreArtifactPath(paths, executablePath)
            || (requireExactManagedPaths
                && !string.Equals(
                    Path.GetFullPath(executablePath),
                    Path.GetFullPath(paths.ManagedCoreExecutable),
                    StringComparison.OrdinalIgnoreCase))
            || !File.Exists(executablePath))
        {
            throw new InvalidDataException("受管 Mihomo 核心路径不存在或包含不受信任的重解析点。");
        }

        if (!IsCoreArtifactPath(paths, metadataPath)
            || (requireExactManagedPaths
                && !string.Equals(
                    Path.GetFullPath(metadataPath),
                    Path.GetFullPath(paths.ManagedCoreMetadata),
                    StringComparison.OrdinalIgnoreCase))
            || !File.Exists(metadataPath)
            || File.GetAttributes(metadataPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("受管 Mihomo 核心缺少可信安装清单。");
        }

        ManagedCoreMetadata metadata;
        try
        {
            await using FileStream stream = File.OpenRead(metadataPath);
            metadata = await JsonSerializer.DeserializeAsync<ManagedCoreMetadata>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("受管 Mihomo 核心安装清单为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("受管 Mihomo 核心安装清单无效。", exception);
        }

        try
        {
            CoreUpdater.ValidateManifest(new CoreUpdateManifest(metadata.Version, metadata.DownloadUri, metadata.ArchiveSha256));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("受管 Mihomo 核心安装清单的来源未经批准。", exception);
        }
        if (!IsSha256(metadata.ExecutableSha256))
        {
            throw new InvalidDataException("受管 Mihomo 核心安装清单中的可执行文件哈希无效。");
        }

        ValidateWindowsAmd64Executable(executablePath);
        await using FileStream executable = File.OpenRead(executablePath);
        string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(executable, cancellationToken));
        if (!actualHash.Equals(metadata.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("受管 Mihomo 核心哈希校验失败。");
        }
    }

    public static void ValidateWindowsAmd64Executable(string path)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length < 0x40)
        {
            throw new InvalidDataException("Mihomo 核心过小，不是有效的 Windows PE 文件。");
        }

        if (stream.Length > MaxExecutableBytes)
        {
            throw new InvalidDataException("Mihomo 核心超过允许的大小限制。");
        }

        Span<byte> dosHeader = stackalloc byte[0x40];
        stream.ReadExactly(dosHeader);
        if (dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
        {
            throw new InvalidDataException("Mihomo 核心不是 Windows 可执行文件。");
        }

        int peHeaderOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader[0x3C..]);
        if (peHeaderOffset < 0 || peHeaderOffset > stream.Length - 6)
        {
            throw new InvalidDataException("Mihomo 核心的 PE 头无效。");
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
            throw new InvalidDataException("Mihomo 核心不是 Windows x64 可执行文件。");
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsCoreArtifactPath(AppPaths paths, string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            return directory is not null
                && string.Equals(
                    directory,
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.CoreRoot)),
                    StringComparison.OrdinalIgnoreCase)
                && !CorePathPolicy.HasReparsePointOnPath(fullPath, paths.ProgramRoot);
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
}
