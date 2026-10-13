using System.Globalization;
using ClashTray.App;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class SettingsPresentationTests
{
    [TestMethod]
    public void SubmittedDraftKeepsLaterEditsAndMergesUneditedSavedFields()
    {
        SettingsDraftController controller = new();
        AppSettings original = new() { Language = "zh-CN", Theme = "system" };
        controller.Edit.Load(original);
        Capture(controller.Edit, original with { Language = "en-US" });
        Assert.IsTrue(controller.TryPrepareSave(original, CultureInfo.InvariantCulture, out SettingsDraftSubmission? submission, out _));
        Assert.IsTrue(submission!.LanguageChanged);

        Capture(controller.Edit, original with { Language = "en-US", Theme = "dark" });
        controller.AcceptSaved(original with { Language = "en-US", AllowLan = true }, submission);

        Assert.AreEqual("dark", controller.Edit.Values.Theme);
        Assert.AreEqual("en-US", controller.Edit.Values.Language);
        Assert.IsTrue(controller.Edit.Values.AllowLan);
        Assert.IsTrue(controller.Edit.IsModified);
    }

    [TestMethod]
    public void InvalidFocusedNumberTextCannotCreateASubmission()
    {
        SettingsDraftController controller = new();
        controller.Edit.Load(new());
        Dictionary<SettingsNumberField, SettingsNumberInput> numbers = controller.Edit.Numbers
            .ToDictionary(pair => pair.Key, pair => pair.Value.Input);
        numbers[SettingsNumberField.MixedPort] = new(7890, "unfinished", HasUncommittedText: true);
        controller.Edit.Capture(controller.Edit.Values, numbers);
        Assert.IsFalse(controller.TryPrepareSave(new(), CultureInfo.InvariantCulture, out SettingsDraftSubmission? submission, out SettingsEditValidationError? error));
        Assert.IsNull(submission);
        Assert.AreEqual(SettingsNumberField.MixedPort, error!.Field);
    }

    [TestMethod]
    public void NetworkPresentationUsesConfirmedStateAndRestrictsUnavailableActions()
    {
        RuntimeSnapshot snapshot = new(new(CoreState.Running, null, null, ProxyMode.Rule, 0, 0, 0, 0, 0, 0, null),
            SystemProxyState.Enabling, TunState.Unknown, SubscriptionState.Idle,
            [], [], [], [], [], [], [], [], null);
        SettingsNetworkPresentation pending = SettingsNetworkPresentation.Create(snapshot, false, false, true, true);
        Assert.IsFalse(pending.SystemProxy.IsOn);
        Assert.IsFalse(pending.SystemProxy.IsEnabled);
        Assert.AreEqual("SwitchStateEnabling", pending.SystemProxy.StateKey);
        Assert.IsFalse(pending.Tun.IsOn);
        Assert.IsFalse(pending.Tun.IsEnabled);

        SettingsNetworkPresentation remote = SettingsNetworkPresentation.Create(
            snapshot with { SystemProxy = SystemProxyState.On, Tun = TunState.On }, false, false, false, false);
        Assert.IsTrue(remote.SystemProxy.IsOn);
        Assert.IsTrue(remote.Tun.IsOn);
        Assert.IsFalse(remote.SystemProxy.IsEnabled);
        Assert.IsFalse(remote.Tun.IsEnabled);
        Assert.IsFalse(remote.CanStartWithAvailableControllerPort);
    }

    private static void Capture(SettingsEditModel edit, AppSettings values) =>
        edit.Capture(values, edit.Numbers.ToDictionary(pair => pair.Key, pair => pair.Value.Input));
}
