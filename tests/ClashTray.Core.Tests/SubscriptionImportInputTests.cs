using ClashTray.App;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class SubscriptionImportInputTests
{
    [TestMethod]
    [DataRow("https://example.invalid/mihomo.yaml", "https://example.invalid/mihomo.yaml")]
    [DataRow("  HTTP://EXAMPLE.INVALID/config  ", "http://example.invalid/config")]
    public void AcceptsAbsoluteHttpUrlsAndTrimsInput(string input, string expected)
    {
        Assert.IsTrue(SubscriptionImportInput.TryParseUri(input, out Uri? uri));
        Assert.IsNotNull(uri);
        Assert.AreEqual(expected, uri.AbsoluteUri);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("not a url")]
    [DataRow("/relative/config.yaml")]
    [DataRow("ftp://example.invalid/config.yaml")]
    [DataRow("file:///C:/config.yaml")]
    [DataRow("https:///missing-host")]
    public void RejectsMissingRelativeOrNonHttpUrls(string? input)
    {
        Assert.IsFalse(SubscriptionImportInput.TryParseUri(input, out Uri? uri));
        Assert.IsNull(uri);
    }
}
