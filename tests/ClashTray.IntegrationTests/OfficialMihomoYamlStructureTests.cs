using ClashTray.Contracts;
using ClashTray.Core;
using ClashTray.Testing;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class OfficialMihomoYamlStructureTests
{
    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task OfficialPinnedMihomoAcceptsSharedStructureCorpusWithEquivalentManagedAndDnsFields()
    {
        string? executable = OfficialMihomoTestSupport.FindMihomoExecutable();
        if (executable is null) { Assert.Inconclusive("An explicitly controlled official Mihomo is required."); return; }
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            string[] keys = ["dns", "'dns'", "\"d\\u006es\"", "\"d\\x6es\"", "\"d\\U0000006es\""];
            foreach (string key in keys)
            {
                string input = Path.Combine(root, "source.yaml");
                string output = Path.Combine(root, "runtime.yaml");
                string yaml = $"---\nmode: direct\nport: 1\n'port': 2\n{key}:\n  enable: true\n  listen: 127.0.0.1:15357\n  nameserver: [1.1.1.1]\ntun: {{ enable: true, stack: gvisor, note: 'one,two', nested: {{ array: [1, 2] }} }}\nproxies: []\nproxy-groups: []\nrules: []\n...\n# trailing\n";
                await File.WriteAllTextAsync(input, yaml);
                await RuntimeConfigBuilder.BuildForCoreStartAsync(input, output, new AppSettings(), null);
                await using MihomoProcessManager process = new();
                Assert.IsTrue(await process.ValidateAsync(executable, output, root), $"Official Mihomo rejected the shared corpus key {key}.");
                MihomoEffectiveListenerPlan plan = await MihomoListenerPlanAnalyzer.AnalyzeEffectiveFileAsync(output);
                Assert.IsTrue(plan.IsComplete, plan.Warning);
                Assert.AreEqual(2, plan.AdditionalListenerPlan.Bindings.Count);
                Assert.IsTrue(plan.AdditionalListenerPlan.Bindings.All(binding => binding.Port == 15357));
                Assert.AreEqual(yaml, await File.ReadAllTextAsync(input));
            }
        });
    }
}
