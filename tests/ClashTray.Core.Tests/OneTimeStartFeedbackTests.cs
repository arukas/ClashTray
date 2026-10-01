using System.Text.RegularExpressions;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class OneTimeStartFeedbackTests
{
    private static string RepositoryRoot
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClashTray.sln")))
            {
                directory = directory.Parent;
            }

            Assert.IsNotNull(directory, "Could not locate the repository root containing ClashTray.sln.");
            return directory.FullName;
        }
    }

    [TestMethod]
    public void OneTimeStartFeedbackUsesThisOperationsTypedOutcome()
    {
        string appSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "ClashTray.App",
            "SettingsPage.xaml.cs"));
        const string handlerName = "private async void StartWithAvailableControllerPortButton_Click";
        int handlerStart = appSource.IndexOf(handlerName, StringComparison.Ordinal);
        Assert.IsTrue(handlerStart >= 0, "The one-time start UI handler is missing.");
        int nextHandler = appSource.IndexOf("\n    private ", handlerStart + handlerName.Length, StringComparison.Ordinal);
        string handler = nextHandler < 0
            ? appSource[handlerStart..]
            : appSource[handlerStart..nextHandler];

        StringAssert.Contains(handler, "result.Outcome switch", StringComparison.Ordinal);
        StringAssert.Contains(handler, "CoreStartOutcome.Started => LocalizationService.Format(", StringComparison.Ordinal);
        foreach (string outcome in new[]
        {
            "AlreadyRunning", "Busy", "CoreMissing", "ConfigurationMissing", "InvalidConfiguration",
            "PortConflict", "ControllerCandidatesExhausted", "TimedOut", "Cancelled"
        })
        {
            StringAssert.Contains(handler, $"CoreStartOutcome.{outcome} =>", StringComparison.Ordinal);
        }

        Assert.IsFalse(Regex.IsMatch(
            handler,
            @"await\s+_runtime\.StartCoreUsingAvailableControllerPortOnceAsync\(\)\s*;\s*StatusText\.Text\s*=\s*LocalizationService\.Get\(\""ControllerPortOneTimeStartSucceeded\""\)",
            RegexOptions.CultureInvariant),
            "A normally completed Task is not proof that the core started; success must come from the typed result.");
    }
}
