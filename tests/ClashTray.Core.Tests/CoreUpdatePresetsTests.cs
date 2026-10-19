namespace ClashTray.Core.Tests;

[TestClass]
public sealed class CoreUpdatePresetsTests
{
    [TestMethod]
    public void CoreUpdatePresetsPinnedManifestPassesValidation()
    {
        CoreUpdater.ValidateManifest(new CoreUpdateManifest(
            CoreUpdatePresets.PinnedVersion,
            CoreUpdatePresets.BuildOfficialDownloadUri(CoreUpdatePresets.PinnedVersion),
            CoreUpdatePresets.PinnedSha256));
    }

    [TestMethod]
    public void CoreUpdatePresetsBuildsCanonicalUriForArbitraryVersion()
    {
        Uri uri = CoreUpdatePresets.BuildOfficialDownloadUri("v1.20.0-rc.1");

        Assert.AreEqual(
            "https://github.com/MetaCubeX/mihomo/releases/download/v1.20.0-rc.1/mihomo-windows-amd64-v1.20.0-rc.1.zip",
            uri.OriginalString);
        CoreUpdater.ValidateManifest(new CoreUpdateManifest("v1.20.0-rc.1", uri, string.Empty));
    }

    [TestMethod]
    public void CoreUpdatePresetsRejectsInvalidVersions()
    {
        foreach (string invalidVersion in new[] { "", "   ", "1.19.31", "v01.19.31", "v1.19.31+build.1", "v1.19.31-", "v1.19.31-rc.01", "v1.19.31/evil" })
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => CoreUpdatePresets.BuildOfficialDownloadUri(invalidVersion),
                invalidVersion);
        }
    }
}
