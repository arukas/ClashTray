using System.Text.Json;
using System.Text.Json.Nodes;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeBindingCompatibilityTests
{
    [TestMethod]
    public void OlderServicePayloadsDeserializeWithMissingListenerMetadata()
    {
        CoreRuntimeBinding current = RuntimeTestHelpers.CreateRuntimeBinding(new AppSettings()) with
        {
            ListenerBindings =
            [
                new RuntimeListenerBinding(
                    "mixed-tcp",
                    "::",
                    17890,
                    RuntimeListenerTransport.Tcp,
                    DualMode: true)
            ]
        };
        JsonObject oldServiceJson = JsonSerializer.SerializeToNode(current)!.AsObject();
        oldServiceJson.Remove(nameof(CoreRuntimeBinding.ListenerBindings));

        CoreRuntimeBinding? oldBinding = JsonSerializer.Deserialize<CoreRuntimeBinding>(oldServiceJson.ToJsonString());

        Assert.IsNotNull(oldBinding);
        Assert.IsNull(oldBinding.ListenerBindings);
        Assert.AreEqual(2, ServiceProtocol.CurrentVersion, "The additive binding metadata must not silently bump the IPC protocol.");
    }

    [TestMethod]
    public void OlderListenerRecordDefaultsNewDualModeFieldToFalse()
    {
        JsonObject oldRecordJson = JsonSerializer.SerializeToNode(new RuntimeListenerBinding(
            "controller",
            "127.0.0.1",
            19091,
            RuntimeListenerTransport.Tcp))!.AsObject();
        oldRecordJson.Remove(nameof(RuntimeListenerBinding.DualMode));

        RuntimeListenerBinding? oldRecord = JsonSerializer.Deserialize<RuntimeListenerBinding>(oldRecordJson.ToJsonString());

        Assert.IsNotNull(oldRecord);
        Assert.IsFalse(oldRecord.DualMode);
        Assert.AreEqual("127.0.0.1", oldRecord.Address);
        Assert.AreEqual(RuntimeListenerTransport.Tcp, oldRecord.Transport);
    }
}
