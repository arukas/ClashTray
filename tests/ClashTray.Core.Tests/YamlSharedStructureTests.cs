using ClashTray.Contracts;
using ClashTray.Testing;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class YamlSharedStructureTests
{
    [TestMethod]
    [DataRow("dns")]
    [DataRow("'dns'")]
    [DataRow("\"d\\u006es\"")]
    [DataRow("\"d\\x6es\"")]
    [DataRow("\"d\\U0000006es\"")]
    public async Task SharedCorpusBuildsEquivalentManagedSettingsAndListenerEvidence(string key)
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            string source = Path.Combine(root, "source.yaml");
            string output = Path.Combine(root, "output.yaml");
            string yaml = $"---\nmode: direct\nport: 1\n'port': 2\n{key}:\n  enable: true\n  listen: 127.0.0.1:15357\n  metadata: 'text # retained'\ntun: {{ note: 'one,two', enable: true, stack: gvisor, nested: {{ array: [1, 2] }} }}\n...\n# trailing\n";
            await File.WriteAllTextAsync(source, yaml);
            await RuntimeConfigBuilder.BuildForCoreStartAsync(source, output, new AppSettings(), null);
            string[] lines = await BoundedYamlReader.ReadFileAsync(output, CancellationToken.None);
            YamlStructureDocument structure = YamlStructureDocument.Read(lines, CancellationToken.None);
            Assert.IsNull(structure.ScopeError);
            Assert.AreEqual(1, structure.Roots.Count(node => node.Key == "port"));
            Assert.AreEqual("''", structure.Roots.Single(node => node.Key == "secret").Value);
            Assert.IsTrue(structure.Roots.Single(node => node.Key == "external-controller").Section.FirstLine < structure.Scope.EndMarkerLine);
            MihomoEffectiveListenerPlan plan = await MihomoListenerPlanAnalyzer.AnalyzeEffectiveFileAsync(output);
            Assert.IsTrue(plan.IsComplete, plan.Warning);
            Assert.AreEqual(2, plan.AdditionalListenerPlan.Bindings.Count);
            Assert.IsTrue(plan.AdditionalListenerPlan.Bindings.All(binding => binding.Port == 15357));
        });
    }

    [TestMethod]
    [DataRow("mode: direct\n---\nport: 1")]
    [DataRow("mode: direct\n...\nport: 1")]
    [DataRow("  mode: direct\n  port: 1")]
    [DataRow("{port: 1, dns: {enable: true}}")]
    public void InvalidDocumentScopeIsSharedAndNeverClaimsListenersAbsent(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        string[] lines = yaml.Split('\n');
        YamlStructureDocument structure = YamlStructureDocument.Read(lines, CancellationToken.None);
        Assert.ThrowsExactly<InvalidDataException>(() => structure.RequireMappingScope());
        MihomoEffectiveListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeEffectiveLines(lines);
        Assert.IsFalse(plan.IsComplete);
        Assert.IsNotNull(plan.Warning);
    }

    [TestMethod]
    public void AnchoredUnmanagedYamlIsPreservedButUnknownDnsDoesNotProduceCompleteEvidence()
    {
        string[] lines = ["defaults: &defaults", "  enable: true", "dns: *defaults", "listeners: []", "allow-lan: false"];
        YamlStructureDocument structure = YamlStructureDocument.Read(lines, CancellationToken.None);
        Assert.IsNull(structure.ScopeError);
        Assert.AreEqual("*defaults", structure.Roots.Single(node => node.Key == "dns").Value);
        Assert.IsFalse(MihomoListenerPlanAnalyzer.AnalyzeLines(lines).IsComplete);
        Assert.ThrowsExactly<InvalidDataException>(() => structure.ReplacementSpan(structure.Roots[0], CancellationToken.None));
    }
}
