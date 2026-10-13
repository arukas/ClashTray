using System.Globalization;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class SettingsEditModelTests
{
    [TestMethod]
    public void PendingPortTextSurvivesExternalThemeAndConfigurationChanges()
    {
        AppSettings initial = new();
        SettingsEditModel model = Loaded(initial);
        Capture(model, initial, SettingsNumberField.HttpPort, new(initial.HttpPort, "7990", true));
        AppSettings external = initial with { Theme = "dark", ActiveConfigurationId = "other", SystemProxyEnabled = true };
        model.MergeExternal(external);
        Assert.AreEqual("7990", model.Numbers[SettingsNumberField.HttpPort].Input.Text);
        Assert.AreEqual("dark", model.Values.Theme);
        AppSettingsPatch patch = Patch(model, external);
        Assert.AreEqual(7990, patch.HttpPort.Value);
        Assert.IsFalse(patch.Theme.IsSpecified);
        Assert.IsFalse(patch.ActiveConfigurationId.IsSpecified);
        Assert.IsFalse(patch.SystemProxyEnabled.IsSpecified);
    }

    [TestMethod]
    public void UserEditedFieldWinsConflictAndCleanNumbersUseLatestValues()
    {
        AppSettings initial = new();
        SettingsEditModel model = Loaded(initial);
        Capture(model, initial with { Theme = "dark" }, SettingsNumberField.HttpPort, new(7990, "7990"));
        AppSettings external = initial with { Theme = "light", HttpPort = 8000, SocksPort = 8001 };
        model.MergeExternal(external);
        AppSettings saved = Patch(model, external).Apply(external);
        Assert.AreEqual("dark", saved.Theme);
        Assert.AreEqual(7990, saved.HttpPort);
        Assert.AreEqual(8001, saved.SocksPort);
        Assert.AreEqual(8001d, model.Numbers[SettingsNumberField.SocksPort].Input.Value);
    }

    [TestMethod]
    public void PatchWithoutSnapshotMergeStillPreservesLatestUntouchedNumbers()
    {
        AppSettings initial = new();
        SettingsEditModel model = Loaded(initial);
        Capture(model, initial with { Theme = "dark" });
        AppSettings latest = initial with { MixedPort = 8991 };
        AppSettingsPatch patch = Patch(model, latest);
        Assert.IsFalse(patch.MixedPort.IsSpecified);
        Assert.AreEqual(8991, patch.Apply(latest).MixedPort);
    }

    [TestMethod]
    [DataRow("", 1d)]
    [DataRow("wrong", 1d)]
    [DataRow("0", 1d)]
    [DataRow("65536", 1d)]
    [DataRow("7.5", 1d)]
    [DataRow("NaN", 1d)]
    [DataRow("Infinity", 1d)]
    public void InvalidPendingTextCannotSaveCommittedOldValue(string text, double value)
    {
        AppSettings initial = new();
        SettingsEditModel model = Loaded(initial);
        Capture(model, initial, SettingsNumberField.HttpPort, new(value, text, true));
        Assert.IsFalse(model.TryCreatePatch(initial, CultureInfo.InvariantCulture, out _, out SettingsEditValidationError? error));
        Assert.AreEqual(SettingsNumberField.HttpPort, error!.Field);
        Assert.AreEqual(text, model.Numbers[SettingsNumberField.HttpPort].Input.Text);
    }

    [TestMethod]
    public void NonFiniteCommittedNumberAndHoursRangeAreRejected()
    {
        AppSettings initial = new();
        SettingsEditModel model = Loaded(initial);
        Capture(model, initial, SettingsNumberField.SocksPort, new(double.NaN, "NaN"));
        Assert.IsFalse(model.TryCreatePatch(initial, CultureInfo.InvariantCulture, out _, out _));
        model.Load(initial);
        Capture(model, initial, SettingsNumberField.SubscriptionRefreshHours, new(169, "169"));
        Assert.IsFalse(model.TryCreatePatch(initial, CultureInfo.InvariantCulture, out _, out SettingsEditValidationError? error));
        Assert.AreEqual(SettingsNumberField.SubscriptionRefreshHours, error!.Field);
    }

    [TestMethod]
    public void SaveFailureKeepsDraftAndSuccessfulSaveAcknowledgesOnlySubmittedEdits()
    {
        AppSettings initial = new();
        SettingsEditModel model = Loaded(initial);
        Capture(model, initial with { Theme = "dark" }, SettingsNumberField.MixedPort, new(initial.MixedPort, "7994", true));
        AppSettingsPatch patch = Patch(model, initial);
        SettingsSaveCheckpoint submitted = model.CaptureSave();
        // A failed command never calls AcceptSaved; a periodic snapshot cannot clear it.
        model.MergeExternal(initial);
        Assert.IsTrue(model.IsModified);
        Assert.AreEqual("7994", model.Numbers[SettingsNumberField.MixedPort].Input.Text);
        Capture(model, model.Values with { Theme = "light" }, SettingsNumberField.MixedPort, new(initial.MixedPort, "7995", true));
        AppSettings saved = patch.Apply(initial);
        model.AcceptSaved(saved, submitted);
        Assert.AreEqual("light", model.Values.Theme);
        Assert.AreEqual(7995, Patch(model, saved).MixedPort.Value);
        Assert.IsTrue(model.IsModified);
    }

    [TestMethod]
    public void SuccessfulSaveClearsUnchangedDraftWithoutRevertingSubmittedNumericValue()
    {
        AppSettings initial = new();
        SettingsEditModel model = Loaded(initial);
        Capture(model, initial, SettingsNumberField.HttpPort, new(initial.HttpPort, "7990", true));
        AppSettingsPatch patch = Patch(model, initial);
        model.AcceptSaved(patch.Apply(initial), model.CaptureSave());
        Assert.AreEqual(7990, model.Values.HttpPort);
        Assert.AreEqual(7990d, model.Numbers[SettingsNumberField.HttpPort].Input.Value);
        Assert.IsFalse(model.IsModified);
        Assert.IsTrue(Patch(model, model.Values).IsEmpty);
    }

    [TestMethod]
    public void OtherActionFieldsNeverBelongToSettingsDraft()
    {
        AppSettings initial = new();
        SettingsEditModel model = Loaded(initial);
        Capture(model, initial with { SystemProxyEnabled = true, TunEnabled = true, ActiveConfigurationId = "other", NakhimovUnlocked = true });
        Assert.IsTrue(Patch(model, initial).IsEmpty);
        Assert.IsFalse(model.IsModified);
    }

    private static SettingsEditModel Loaded(AppSettings settings)
    {
        SettingsEditModel model = new();
        model.Load(settings);
        return model;
    }

    private static void Capture(SettingsEditModel model, AppSettings values, SettingsNumberField? field = null, SettingsNumberInput? input = null)
    {
        Dictionary<SettingsNumberField, SettingsNumberInput> numbers = model.Numbers.ToDictionary(pair => pair.Key, pair => pair.Value.Input);
        if (field is SettingsNumberField selected) { numbers[selected] = input!; }
        model.Capture(values, numbers);
    }

    private static AppSettingsPatch Patch(SettingsEditModel model, AppSettings latest)
    {
        Assert.IsTrue(model.TryCreatePatch(latest, CultureInfo.InvariantCulture, out AppSettingsPatch patch, out SettingsEditValidationError? error), error?.Detail);
        return patch;
    }
}
