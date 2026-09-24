using System.Text.RegularExpressions;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeConfigBuilderYamlCorpusTests
{
    [TestMethod]
    public async Task QuotedAndWhitespaceManagedRootKeysAreReplacedWithoutDuplicates()
    {
        string output = await BuildAsync(
            "'port' : 8011" + Environment.NewLine
            + "allow-lan : true" + Environment.NewLine
            + "'ipv6': true" + Environment.NewLine
            + "other: keep",
            new AppSettings(HttpPort: 7892, AllowLan: false, Ipv6: false));

        Assert.AreEqual(1, CountRootKey(output, "port"));
        Assert.AreEqual(1, CountRootKey(output, "allow-lan"));
        Assert.AreEqual(1, CountRootKey(output, "ipv6"));
        Assert.IsTrue(output.Contains("port: 7892", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("allow-lan: false", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("ipv6: false", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("other: keep", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RepeatedManagedRootKeysAreRemovedAcrossWholeNodesAndKeepNeighborComments()
    {
        string source = "mode: direct" + Environment.NewLine
            + "# keep comment between controller values" + Environment.NewLine
            + "external-controller: 0.0.0.0:1" + Environment.NewLine
            + "external-controller: |-" + Environment.NewLine
            + "  0.0.0.0:2" + Environment.NewLine
            + "  stale controller" + Environment.NewLine
            + "# keep comment between secret values" + Environment.NewLine
            + "secret: stale-first" + Environment.NewLine
            + "secret: >-" + Environment.NewLine
            + "  stale-secret" + Environment.NewLine
            + "proxies: []" + Environment.NewLine
            + "proxy-groups: []" + Environment.NewLine
            + "rules: []";

        string output = await BuildAsync(source, new AppSettings(ControllerPort: 9193));

        Assert.AreEqual(1, CountRootKey(output, "external-controller"));
        Assert.AreEqual(1, CountRootKey(output, "secret"));
        Assert.IsTrue(output.Contains("# keep comment between controller values", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("# keep comment between secret values", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("0.0.0.0:1", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("0.0.0.0:2", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("stale controller", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("stale-first", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("stale-secret", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("external-controller: 127.0.0.1:9193", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("secret: ''", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("proxies: []", StringComparison.Ordinal));
    }
    [TestMethod]
    public async Task BlockTunMappingChangesOnlyDirectPropertiesAndPreservesNestedAndBlockScalars()
    {
        const string source = """
            tun:
                dns:
                  enable: false
                  stack: system
                metadata: |-
                  enable: textual
                  stack: preserved
                enable: false
            proxy-groups:
              - name: choice
                type: select
                proxies: [DIRECT]
            """;

        string output = await BuildAsync(
            source,
            new AppSettings(TunEnabled: true, TunStack: "mixed"));

        Assert.IsTrue(output.Contains("    dns:", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("      enable: false", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("      stack: system", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("    metadata: |-", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("      enable: textual", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("      stack: preserved", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("    enable: true", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("    stack: mixed", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("  - name: choice", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("    type: select", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("    proxies: [DIRECT]", StringComparison.Ordinal));
        Assert.AreEqual(1, CountTunDirectKey(output, "enable"));
        Assert.AreEqual(1, CountTunDirectKey(output, "stack"));
    }

    [TestMethod]
    public async Task InlineTunMappingHandlesNestedCollectionsAndCommasInsideQuotes()
    {
        const string source =
            "tun: { note: \"one,two\", dns: { enable: false, stack: system }, enable: false, stack: system }";

        string output = await BuildAsync(
            source,
            new AppSettings(TunEnabled: true, TunStack: "mixed"));

        Assert.IsTrue(output.Contains("note: \"one,two\"", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("dns: { enable: false, stack: system }", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("enable: true", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("stack: mixed", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains(
            """tun: { note: "one,two", dns: { enable: false, stack: system }, enable: true, stack: mixed }""",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AnchorsAliasesCommentsProxyGroupsAndRulesOutsideManagedFieldsArePreserved()
    {
        const string source = """
            defaults: &defaults
              skip-auth: true
            defaults-copy: *defaults
            tun: # comment kept
              enable: false # direct key
            proxy-groups:
              - name: select
                type: select
                proxies: [DIRECT, "node, with comma"]
                metadata:
                  port: 1234
            rules:
              - DOMAIN-SUFFIX,example.com,DIRECT # comment
            """;

        string output = await BuildAsync(
            source,
            new AppSettings(TunEnabled: false));

        Assert.IsTrue(output.Contains("defaults: &defaults", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("defaults-copy: *defaults", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("tun: # comment kept", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("  enable: false # direct key", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("proxies: [DIRECT, \"node, with comma\"]", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("      port: 1234", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("- DOMAIN-SUFFIX,example.com,DIRECT # comment", StringComparison.Ordinal));
        Assert.AreEqual(1, CountRootKey(output, "external-controller"));
        Assert.AreEqual(1, CountRootKey(output, "secret"));
        Assert.IsTrue(output.Contains("external-controller: 127.0.0.1:9090", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("secret: ''", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ManagedMultilineNodesAreRemovedWholeAndPreserveAdjacentRootContent()
    {
        const string source = """
            secret: |-
              stale block secret
              port: 1111
            external-controller: >-
              127.0.0.1:10001
              stale controller continuation
            external-ui-name: 'old
              multiline name'
            external-ui-url: "https://old.example/
              old token"
            mode: rule
            proxies: []
            proxy-groups: []
            rules: []
            tun:
              enable: false
            """;

        string output = await BuildAsync(source, new AppSettings(ControllerPort: 9192, HttpPort: 7893));

        Assert.IsFalse(output.Contains("stale block secret", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("stale controller continuation", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("multiline name", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("old token", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("port: 1111", StringComparison.Ordinal));
        Assert.AreEqual(1, CountRootKey(output, "secret"));
        Assert.AreEqual(1, CountRootKey(output, "external-controller"));
        Assert.AreEqual(0, CountRootKey(output, "external-ui-name"));
        Assert.AreEqual(0, CountRootKey(output, "external-ui-url"));
        Assert.IsTrue(output.Contains("external-controller: 127.0.0.1:9192", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("secret: ''", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("mode: rule", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("proxies: []", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("rules: []", StringComparison.Ordinal));
    }


    [TestMethod]
    public async Task ExplicitDocumentEndKeepsManagedSettingsInsideDocumentAndReplacesTunBlockValue()
    {
        string source = "---" + Environment.NewLine
            + "mode: direct" + Environment.NewLine
            + "allow-lan: true" + Environment.NewLine
            + "tun:" + Environment.NewLine
            + "  enable: false" + Environment.NewLine
            + "  stack: |-" + Environment.NewLine
            + "    gvisor" + Environment.NewLine
            + "proxies: []" + Environment.NewLine
            + "proxy-groups: []" + Environment.NewLine
            + "rules: []" + Environment.NewLine
            + "..." + Environment.NewLine
            + "# trailing comment";

        string output = await BuildAsync(
            source,
            new AppSettings(AllowLan: false, TunEnabled: false, TunStack: "system"));
        string[] lines = output.Split(["\r\n", "\n"], StringSplitOptions.None);
        int endMarker = Array.IndexOf(lines, "...");

        Assert.IsTrue(endMarker > 0, "The explicit document end marker must be retained.");
        Assert.IsTrue(lines.Take(endMarker).Any(line => line == "external-controller: 127.0.0.1:9090"));
        Assert.IsTrue(lines.Take(endMarker).Any(line => line == "allow-lan: false"));
        Assert.IsTrue(lines.Take(endMarker).Any(line => line == "  stack: system"));
        Assert.IsFalse(output.Contains("gvisor", StringComparison.Ordinal));
        Assert.IsTrue(output.TrimEnd().EndsWith("# trailing comment", StringComparison.Ordinal));
        Assert.AreEqual(1, CountRootKey(output, "tun"));
    }

    [TestMethod]
    public async Task IndentedRootMappingIsRejectedBeforeReplacingLastSuccessfulOutput()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string sourcePath = Path.Combine(root, "source.yaml");
        string destinationPath = Path.Combine(root, "effective.yaml");
        string source = "---" + Environment.NewLine
            + "  mode: direct" + Environment.NewLine
            + "  allow-lan: true" + Environment.NewLine
            + "  tun:" + Environment.NewLine
            + "    enable: false" + Environment.NewLine
            + "    stack: |" + Environment.NewLine
            + "      gvisor" + Environment.NewLine
            + "  proxies: []" + Environment.NewLine
            + "  proxy-groups: []" + Environment.NewLine
            + "  rules: []" + Environment.NewLine
            + "...";
        const string lastGood = "last-successful-output";

        try
        {
            await File.WriteAllTextAsync(sourcePath, source);
            await File.WriteAllTextAsync(destinationPath, lastGood);

            InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                RuntimeConfigBuilder.BuildAsync(sourcePath, destinationPath, new AppSettings()));

            StringAssert.Contains(exception.Message, "overall-indented", StringComparison.Ordinal);
            Assert.AreEqual(source, await File.ReadAllTextAsync(sourcePath));
            Assert.AreEqual(lastGood, await File.ReadAllTextAsync(destinationPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task MultipleYamlDocumentsAreRejectedBeforeReplacingLastSuccessfulOutput()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string sourcePath = Path.Combine(root, "source.yaml");
        string destinationPath = Path.Combine(root, "effective.yaml");
        string source = "---" + Environment.NewLine
            + "mode: direct" + Environment.NewLine
            + "..." + Environment.NewLine
            + "---" + Environment.NewLine
            + "mode: global";
        const string lastGood = "last-successful-output";

        try
        {
            await File.WriteAllTextAsync(sourcePath, source);
            await File.WriteAllTextAsync(destinationPath, lastGood);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => RuntimeConfigBuilder.BuildAsync(
                sourcePath,
                destinationPath,
                new AppSettings()));

            Assert.AreEqual(source, await File.ReadAllTextAsync(sourcePath));
            Assert.AreEqual(lastGood, await File.ReadAllTextAsync(destinationPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task TunQuotedMultilineStackValueIsReplacedAsOneNode()
    {
        string source = "mode: direct" + Environment.NewLine
            + "tun:" + Environment.NewLine
            + "  enable: false" + Environment.NewLine
            + "  stack: \"gvisor" + Environment.NewLine
            + "    stale continuation\"" + Environment.NewLine
            + "  mtu: 1400 # keep comment" + Environment.NewLine;

        string output = await BuildAsync(source, new AppSettings(TunStack: "system"));

        Assert.IsFalse(output.Contains("gvisor", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("stale continuation", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("  stack: system", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("  mtu: 1400 # keep comment", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UnsupportedManagedAnchorsAndMultilineFlowNodesAreRejectedWithoutReplacingOutput()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string sourcePath = Path.Combine(root, "source.yaml");
        string destinationPath = Path.Combine(root, "effective.yaml");
        const string lastGood = "last-successful-output";
        string[] unsupported =
        [
            "secret: &shared old-secret" + Environment.NewLine + "copy: *shared",
            "secret: {" + Environment.NewLine + "  nested: [unbalanced" + Environment.NewLine + "mode: rule",
            "tun: |-" + Environment.NewLine + "  enable: true"
        ];

        try
        {
            await File.WriteAllTextAsync(destinationPath, lastGood);
            foreach (string input in unsupported)
            {
                await File.WriteAllTextAsync(sourcePath, input);
                InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                    () => RuntimeConfigBuilder.BuildAsync(
                        sourcePath,
                        destinationPath,
                        new AppSettings(TunEnabled: true)));
                Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Message));
                Assert.AreEqual(lastGood, await File.ReadAllTextAsync(destinationPath));
                Assert.AreEqual(input, await File.ReadAllTextAsync(sourcePath));
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task GeneratedCoreStartConfigAlwaysKeepsTunDisabled()
    {
        string output = await BuildAsync(
            "tun:" + Environment.NewLine + "  enable: true",
            new AppSettings(TunEnabled: true, TunStack: "mixed"),
            coreStart: true);

        Assert.IsTrue(output.Contains("enable: false", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("enable: true", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UnsupportedTunAliasAndUnbalancedFlowMapAreRejectedWithoutReplacingLastOutput()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string sourcePath = Path.Combine(root, "source.yaml");
        string destinationPath = Path.Combine(root, "effective.yaml");
        const string lastGood = "last-successful-output";
        try
        {
            await File.WriteAllTextAsync(destinationPath, lastGood);
            string[] unsupported =
            [
                "defaults: &defaults" + Environment.NewLine + "  enable: false" + Environment.NewLine + "tun: *defaults",
                "tun: { enable: true"
            ];

            foreach (string source in unsupported)
            {
                await File.WriteAllTextAsync(sourcePath, source);
                InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                    () => RuntimeConfigBuilder.BuildAsync(
                        sourcePath,
                        destinationPath,
                        new AppSettings(TunEnabled: true)));
                Assert.IsTrue(exception.Message.Length is > 0 and <= 256);
                Assert.AreEqual(lastGood, await File.ReadAllTextAsync(destinationPath));
                Assert.AreEqual(source, await File.ReadAllTextAsync(sourcePath));
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
    private static async Task<string> BuildAsync(
        string source,
        AppSettings settings,
        bool coreStart = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string sourcePath = Path.Combine(root, "source.yaml");
        string destinationPath = Path.Combine(root, "effective.yaml");
        try
        {
            await File.WriteAllTextAsync(sourcePath, source);
            if (coreStart)
            {
                await RuntimeConfigBuilder.BuildForCoreStartAsync(
                    sourcePath,
                    destinationPath,
                    settings,
                    externalUiPath: null);
            }
            else
            {
                await RuntimeConfigBuilder.BuildAsync(sourcePath, destinationPath, settings);
            }

            string output = await File.ReadAllTextAsync(destinationPath);
            Assert.AreEqual(source, await File.ReadAllTextAsync(sourcePath));
            return output;
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static int CountRootKey(string yaml, string key) =>
        yaml.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Count(line => Regex.IsMatch(
                line,
                $@"^{Regex.Escape(key)}\s*:",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || Regex.IsMatch(
                    line,
                    $@"^['""]{Regex.Escape(key)}['""]\s*:",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    private static int CountTunDirectKey(string yaml, string key)
    {
        bool inTun = false;
        int tunIndent = 0;
        int count = 0;
        foreach (string line in yaml.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int indent = line.Length - line.TrimStart().Length;
            string trimmed = line.TrimStart();
            if (indent == 0)
            {
                inTun = trimmed.StartsWith("tun:", StringComparison.OrdinalIgnoreCase);
                tunIndent = indent;
                if (inTun && trimmed.Contains('{', StringComparison.Ordinal))
                {
                    return CountFlowKey(trimmed, key);
                }

                continue;
            }

            if (!inTun || indent != tunIndent + 4)
            {
                continue;
            }

            if (Regex.IsMatch(trimmed, $@"^['""]?{Regex.Escape(key)}['""]?\s*:"))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountFlowKey(string yaml, string key) =>
        Regex.Count(yaml, $@"(?<![\w-])['""]?{Regex.Escape(key)}['""]?\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}