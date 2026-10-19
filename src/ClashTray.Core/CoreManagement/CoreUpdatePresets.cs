namespace ClashTray.Core;

public static class CoreUpdatePresets
{
    // Keep in sync with packaging/mihomo-release.json.
    public const string PinnedVersion = "v1.19.31";
    public const string PinnedSha256 = "38b2420799d9e7cde77ec1a19c7150dd17ca77f7fb82d9f62cb8763a307eee67";

    public static Uri BuildOfficialDownloadUri(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Uri uri = new(
            $"https://github.com/MetaCubeX/mihomo/releases/download/{version}/mihomo-windows-amd64-{version}.zip",
            UriKind.Absolute);
        CoreUpdater.ValidateManifest(new CoreUpdateManifest(version, uri, string.Empty));
        return uri;
    }
}
