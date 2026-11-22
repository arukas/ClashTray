using ClashTray.App;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class PanelDisplayFormattingTests
{
    [TestMethod]
    [DataRow("0.3.9+28dbbb7e3448", "0.3.9")]
    [DataRow("0.3.10-alpha.1+abcdef", "0.3.10-alpha.1")]
    [DataRow("0.3.9", "0.3.9")]
    [DataRow("", "")]
    public void ApplicationVersionOmitsSourceRevision(string version, string expected)
    {
        Assert.AreEqual(expected, PanelDisplayFormatting.NormalizeInformationalVersion(version));
    }

    [TestMethod]
    [DataRow(0d, "0 B/s")]
    [DataRow(1536d, "1.5 KB/s")]
    [DataRow(2d * 1024 * 1024, "2 MB/s")]
    public void RateFormattingUsesBinaryUnits(double bytesPerSecond, string expected)
    {
        Assert.AreEqual(expected, PanelDisplayFormatting.FormatRate(bytesPerSecond));
    }

    [TestMethod]
    [DataRow(0L, "0 KB")]
    [DataRow(1536L, "1.5 KB")]
    [DataRow(2L * 1024 * 1024, "2 MB")]
    public void ByteFormattingUsesBinaryUnits(long bytes, string expected)
    {
        Assert.AreEqual(expected, PanelDisplayFormatting.FormatBytes(bytes));
    }
}
