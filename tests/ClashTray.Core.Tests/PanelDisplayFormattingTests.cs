using ClashTray.App;
using ClashTray.Contracts;

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
    [DataRow(CoreState.Missing, "Neutral")]
    [DataRow(CoreState.Stopped, "Neutral")]
    [DataRow(CoreState.Validating, "Pending")]
    [DataRow(CoreState.Starting, "Pending")]
    [DataRow(CoreState.Running, "Running")]
    [DataRow(CoreState.Stopping, "Pending")]
    [DataRow(CoreState.Restarting, "Pending")]
    [DataRow(CoreState.Failed, "Failed")]
    [DataRow((CoreState)999, "Neutral")]
    public void CoreStatesMapToSemanticTones(CoreState state, string expected)
    {
        Assert.AreEqual(Enum.Parse<PanelStatusTone>(expected), PanelStatusPresentation.CoreTone(state));
    }

    [TestMethod]
    [DataRow(EndpointSessionState.Disconnected, "Neutral")]
    [DataRow(EndpointSessionState.Connecting, "Pending")]
    [DataRow(EndpointSessionState.Connected, "Running")]
    [DataRow(EndpointSessionState.Reconnecting, "Pending")]
    [DataRow(EndpointSessionState.AuthenticationFailed, "Failed")]
    [DataRow(EndpointSessionState.CertificateFailed, "Failed")]
    [DataRow(EndpointSessionState.Incompatible, "Failed")]
    [DataRow(EndpointSessionState.Failed, "Failed")]
    [DataRow((EndpointSessionState)999, "Neutral")]
    public void EndpointStatesMapToSemanticTones(EndpointSessionState state, string expected)
    {
        Assert.AreEqual(Enum.Parse<PanelStatusTone>(expected), PanelStatusPresentation.EndpointTone(state));
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
