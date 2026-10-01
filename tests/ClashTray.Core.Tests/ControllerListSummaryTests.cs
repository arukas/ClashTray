using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ControllerListSummaryTests
{
    [TestMethod]
    public void InvalidAndDuplicateConnectionsAreExcludedFromLoadedCountButRemainReported()
    {
        using JsonDocument document = JsonDocument.Parse("{\"connections\":[null,{}, {\"id\":\"a\"}, {\"id\":\"a\"}]}");
        ControllerListData<ConnectionInfo> result = MihomoDataParser.ParseConnectionsWithSummary(document);
        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual(new ControllerListSummary(4, false), result.Summary);
    }

    [TestMethod]
    [DataRow(2000, false)]
    [DataRow(2001, true)]
    public void ConnectionTruncationBoundaryMatchesTheInspectedPrefix(int count, bool truncated)
    {
        using JsonDocument document = JsonDocument.Parse("{\"connections\":[" + string.Join(',', Enumerable.Range(0, count).Select(i => $"{{\"id\":\"{i}\"}}")) + "]}");
        ControllerListData<ConnectionInfo> result = MihomoDataParser.ParseConnectionsWithSummary(document);
        Assert.AreEqual(Math.Min(count, 2000), result.Items.Count);
        Assert.AreEqual(new ControllerListSummary(count, truncated), result.Summary);
    }
}
