using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class EndpointHandshakeValidatorTests
{
    [TestMethod]
    public void ValidVersionResponseEnablesRemoteControllerCapabilities()
    {
        using JsonDocument document = JsonDocument.Parse("{\"version\":\"v1.19.30\"}");

        EndpointHandshakeResult result = EndpointHandshakeValidator.Validate(document);

        Assert.IsTrue(result.IsCompatible);
        Assert.AreEqual(EndpointSessionState.Connected, result.State);
        Assert.AreEqual("v1.19.30", result.Version);
        Assert.AreEqual(EndpointCapabilityDefaults.Remote, result.Capabilities);
        Assert.IsNull(result.ErrorMessage);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("[]")]
    [DataRow("{\"version\":17}")]
    [DataRow("{\"version\":null}")]
    public void MissingOrWrongVersionResponseIsIncompatible(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);

        EndpointHandshakeResult result = EndpointHandshakeValidator.Validate(document);

        Assert.IsFalse(result.IsCompatible);
        Assert.AreEqual(EndpointSessionState.Incompatible, result.State);
        Assert.AreEqual(EndpointCapability.None, result.Capabilities);
        Assert.IsNull(result.Version);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [TestMethod]
    public void OversizedVersionIsRejectedBeforeItReachesTheSnapshot()
    {
        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.Serialize(new { version = new string('v', 129) }));

        EndpointHandshakeResult result = EndpointHandshakeValidator.Validate(document);

        Assert.AreEqual(EndpointSessionState.Incompatible, result.State);
        Assert.IsNull(result.Version);
        Assert.AreEqual(EndpointCapability.None, result.Capabilities);
    }
}
