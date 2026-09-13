using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ClashTray.Core.Tests;

/// <summary>
/// File-based consistency checks for the App layer localization (LOC-001..006):
/// the zh-CN and en-US MRT resources must expose the same keys with matching
/// placeholders, and no user-visible Chinese text may remain in XAML or App
/// code strings (the smoke test's synthetic node names are the one exception).
/// </summary>
[TestClass]
public sealed class LocalizationResourceTests
{
    private static readonly Regex CjkIdeograph = new(@"[一-鿿]", RegexOptions.Compiled);
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.Compiled);

    private static string RepositoryRoot
    {
        get
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClashTray.sln")))
            {
                directory = directory.Parent;
            }

            Assert.IsNotNull(directory, "Could not locate the repository root containing ClashTray.sln.");
            return directory.FullName;
        }
    }

    private static Dictionary<string, string> LoadResourceValues(string language)
    {
        string path = Path.Combine(RepositoryRoot, "src", "ClashTray.App", "Strings", language, "Resources.resw");
        Assert.IsTrue(File.Exists(path), $"Missing resource file: {path}");
        return XDocument.Load(path)
            .Root!
            .Elements("data")
            .ToDictionary(element => element.Attribute("name")!.Value, element => element.Element("value")!.Value, StringComparer.Ordinal);
    }

    private static string[] PlaceholdersOf(string value) =>
        Placeholder.Matches(value).Select(match => match.Value).Order(StringComparer.Ordinal).ToArray();

    [TestMethod]
    public void BothLanguagesExposeIdenticalKeySetsWithNonEmptyValues()
    {
        Dictionary<string, string> chinese = LoadResourceValues("zh-CN");
        Dictionary<string, string> english = LoadResourceValues("en-US");

        Assert.IsTrue(chinese.Count >= 100, $"Expected a complete resource set, found only {chinese.Count} keys.");
        CollectionAssert.AreEquivalent(chinese.Keys.ToArray(), english.Keys.ToArray());
        foreach ((string key, string value) in chinese)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(value), $"Empty zh-CN value for {key}.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(english[key]), $"Empty en-US value for {key}.");
            CollectionAssert.AreEqual(
                PlaceholdersOf(value),
                PlaceholdersOf(english[key]),
                $"Format placeholder mismatch for {key}.");
        }
    }

    [TestMethod]
    public void EnglishResourcesContainNoChineseIdeographs()
    {
        foreach ((string key, string value) in LoadResourceValues("en-US"))
        {
            // Language names are intentionally written in their own language.
            if (key is "LanguageZhCNItem.Content")
            {
                continue;
            }

            Assert.IsFalse(CjkIdeograph.IsMatch(value), $"en-US value for {key} contains Chinese: {value}");
        }
    }

    [TestMethod]
    public void CriticalKeysExistInBothLanguages()
    {
        string[] required =
        [
            "CoreStateRunning", "CoreStateStopped", "SwitchStateOn", "SwitchStateOff",
            "DialogCancel", "MenuQuit", "TrayTooltipStopped", "MenuEnableSystemProxy", "MenuDisableTun",
            "LanguageRestartPrompt", "ConfigImportPrompt", "DelayTimeout", "DelayNotTested", "CurrentLabel",
            "QuitButton.Content", "SystemProxySwitch.AutomationProperties.Name", "LanguageBox.Header"
        ];
        Dictionary<string, string> chinese = LoadResourceValues("zh-CN");
        Dictionary<string, string> english = LoadResourceValues("en-US");
        foreach (string key in required)
        {
            Assert.IsTrue(chinese.ContainsKey(key), $"zh-CN missing key {key}.");
            Assert.IsTrue(english.ContainsKey(key), $"en-US missing key {key}.");
        }
    }

    private static IEnumerable<string> EnumerateAppSourceFiles(string pattern)
    {
        string appDirectory = Path.Combine(RepositoryRoot, "src", "ClashTray.App");
        return Directory.EnumerateFiles(appDirectory, pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void AppXamlFilesContainNoChineseIdeographs()
    {
        foreach (string file in EnumerateAppSourceFiles("*.xaml"))
        {
            Assert.IsFalse(CjkIdeograph.IsMatch(File.ReadAllText(file)),
                $"{Path.GetFileName(file)} still embeds Chinese text; move it into Resources.resw and reference it with x:Uid.");
        }
    }

    [TestMethod]
    public void AppCodeContainsNoChineseIdeographsOutsideTheSmokeTest()
    {
        foreach (string file in EnumerateAppSourceFiles("*.cs"))
        {
            // The smoke test's synthetic configuration names and log samples are
            // data for rendering checks, not UI text, and stay Chinese.
            if (string.Equals(Path.GetFileName(file), "MainWindow.SmokeTest.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assert.IsFalse(CjkIdeograph.IsMatch(File.ReadAllText(file)),
                $"{Path.GetFileName(file)} still embeds Chinese text; route user-visible strings through LocalizationService.");
        }
    }

    [TestMethod]
    public void EveryXamlUidHasAResourceEntry()
    {
        Dictionary<string, string> chinese = LoadResourceValues("zh-CN");
        Regex uidPattern = new Regex(@"x:Uid=""(?<uid>[A-Za-z0-9_]+)""", RegexOptions.Compiled);
        List<string> missing = new List<string>();
        foreach (string file in EnumerateAppSourceFiles("*.xaml"))
        {
            foreach (Match match in uidPattern.Matches(File.ReadAllText(file)))
            {
                string uid = match.Groups["uid"].Value;
                if (!chinese.Keys.Any(key => key.StartsWith(uid + ".", StringComparison.Ordinal)))
                {
                    missing.Add($"{Path.GetFileName(file)}: {uid}");
                }
            }
        }

        Assert.AreEqual(0, missing.Count, $"x:Uid values without resource entries: {string.Join("; ", missing)}");
    }
}
