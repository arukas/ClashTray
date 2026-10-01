using System.Text.RegularExpressions;
using ClashTray.App;

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

        StringAssert.Contains(handler, "CoreStartFeedback.Create(result)", StringComparison.Ordinal);
        StringAssert.Contains(handler, "feedback.ErrorDetail", StringComparison.Ordinal);

        Assert.IsFalse(Regex.IsMatch(
            handler,
            @"await\s+_runtime\.StartCoreUsingAvailableControllerPortOnceAsync\(\)\s*;\s*StatusText\.Text\s*=\s*LocalizationService\.Get\(\""ControllerPortOneTimeStartSucceeded\""\)",
            RegexOptions.CultureInvariant),
            "A normally completed Task is not proof that the core started; success must come from the typed result.");
    }

    [TestMethod]
    [DataRow(CoreStartOutcome.Started, "Succeeded")]
    [DataRow(CoreStartOutcome.AlreadyRunning, "AlreadyRunning")]
    [DataRow(CoreStartOutcome.Busy, "Busy")]
    [DataRow(CoreStartOutcome.CoreMissing, "CoreMissing")]
    [DataRow(CoreStartOutcome.ConfigurationMissing, "ConfigurationMissing")]
    [DataRow(CoreStartOutcome.InvalidConfiguration, "InvalidConfiguration")]
    [DataRow(CoreStartOutcome.PortConflict, "PortConflict")]
    [DataRow(CoreStartOutcome.ControllerCandidatesExhausted, "CandidatesExhausted")]
    [DataRow(CoreStartOutcome.TimedOut, "TimedOut")]
    [DataRow(CoreStartOutcome.Cancelled, "Cancelled")]
    [DataRow(CoreStartOutcome.Failed, "Failed")]
    public void FeedbackUsesOutcomeAndOnlyConfirmedSuccessHasAPort(CoreStartOutcome outcome, string suffix)
    {
        CoreStartFeedback feedback = CoreStartFeedback.Create(new CoreStartOperationResult(Guid.NewGuid(), outcome, 51999,
            ErrorMessage: "https://example.test/subscription?token=secret-value"));
        Assert.AreEqual("ControllerPortOneTimeStart" + suffix, feedback.ResourceKey);
        Assert.AreEqual(outcome == CoreStartOutcome.Started ? 51999 : (int?)null, feedback.ConfirmedPort);
        if (outcome is CoreStartOutcome.Started or CoreStartOutcome.AlreadyRunning)
        {
            Assert.IsNull(feedback.ErrorDetail);
        }
        else
        {
            Assert.IsNotNull(feedback.ErrorDetail);
            Assert.IsFalse(feedback.ErrorDetail.Contains("secret-value", StringComparison.Ordinal));
            StringAssert.Contains(feedback.ErrorDetail, "example.test", StringComparison.Ordinal);
        }
    }
}
