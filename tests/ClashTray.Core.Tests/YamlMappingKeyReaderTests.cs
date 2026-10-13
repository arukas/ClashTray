namespace ClashTray.Core.Tests;

[TestClass]
public sealed class YamlMappingKeyReaderTests
{
    [TestMethod]
    [DataRow("dns:", "dns")]
    [DataRow("\"d\\u006es\":", "dns")]
    [DataRow("\"d\\x6es\":", "dns")]
    [DataRow("\"d\\U0000006es\":", "dns")]
    [DataRow("'d\\u006es':", "d\\u006es")]
    [DataRow("'a''b':", "a'b")]
    [DataRow("\"a:b\":", "a:b")]
    [DataRow("\"a\\\\u006e\":", "a\\u006e")]
    [DataRow("\"\\U0001F600\":", "😀")]
    [DataRow("中文:", "中文")]
    public void ScalarKeysPreserveYamlQuotingSemantics(string line, string expected)
    {
        Assert.IsTrue(YamlMappingKeyReader.TryRead(line, out string key, out _));
        Assert.AreEqual(expected, key);
    }

    [TestMethod]
    [DataRow("\"d\\qns\":")]
    [DataRow("\"d\\x6\":")]
    [DataRow("\"d\\U00110000\":")]
    [DataRow("\"d\\U0000D800\":")]
    [DataRow("\"d\\UFFFFFFFF\":")]
    [DataRow("\"d\\u0000\":")]
    [DataRow("!!str dns:")]
    [DataRow("&key dns:")]
    [DataRow("*key:")]
    [DataRow("? dns:")]
    [DataRow("{dns: false}")]
    [DataRow("\"dns\" trailing:")]
    [DataRow("'d'n's':")]
    public void UnsupportedOrMalformedKeysAreNotGuessed(string line) =>
        Assert.IsFalse(YamlMappingKeyReader.TryRead(line, out _, out _));
}
