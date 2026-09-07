using ClashTray.Core;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ConfigurationStoreTests
{
    private static readonly int[] ExpectedNewestItems = [2, 3];

    [TestMethod]
    public void ValidateYamlAcceptsMihomoConfiguration()
    {
        ConfigurationStore.ValidateYaml("mixed-port: 7890\nmode: rule\n"u8);
    }

    [TestMethod]
    public void ValidateYamlRejectsEmptyContent()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => ConfigurationStore.ValidateYaml(ReadOnlySpan<byte>.Empty));
    }

    [TestMethod]
    public void BoundedBufferRetainsNewestItems()
    {
        var buffer = new BoundedBuffer<int>(2);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        CollectionAssert.AreEqual(ExpectedNewestItems, buffer.Snapshot().ToArray());
    }
}
