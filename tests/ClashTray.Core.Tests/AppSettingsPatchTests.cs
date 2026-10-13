using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class AppSettingsPatchTests
{
    [TestMethod]
    public void ApplyDistinguishesUnspecifiedFromExplicitFalseNullAndZero()
    {
        AppSettings current = new(
            ActiveConfigurationId: "profile",
            AllowLan: true,
            SystemProxyEnabled: true,
            SubscriptionRefreshHours: 24,
            TunEnabled: true);
        AppSettingsPatch patch = new(
            ActiveConfigurationId: SettingPatchValue.Set<string?>(null),
            AllowLan: SettingPatchValue.Set(false),
            SystemProxyEnabled: SettingPatchValue.Set(false),
            SubscriptionRefreshHours: SettingPatchValue.Set(0));

        AppSettings merged = patch.Apply(current);

        Assert.IsTrue(patch.ActiveConfigurationId.IsSpecified);
        Assert.IsTrue(patch.AllowLan.IsSpecified);
        Assert.IsTrue(patch.SubscriptionRefreshHours.IsSpecified);
        Assert.IsNull(merged.ActiveConfigurationId);
        Assert.IsFalse(merged.AllowLan);
        Assert.IsFalse(merged.SystemProxyEnabled);
        Assert.AreEqual(0, merged.SubscriptionRefreshHours);
        Assert.IsTrue(merged.TunEnabled);
        Assert.IsTrue(new AppSettingsPatch().IsEmpty);
    }

    [TestMethod]
    public void DiffMarksOnlyChangedValuesAndCanRepresentClears()
    {
        AppSettings baseline = new(
            ActiveConfigurationId: "profile",
            AllowLan: true,
            SubscriptionRefreshHours: 24);
        AppSettings proposed = baseline with
        {
            ActiveConfigurationId = null,
            AllowLan = false,
            SubscriptionRefreshHours = 0
        };

        AppSettingsPatch patch = AppSettingsPatch.Diff(baseline, proposed);

        Assert.IsTrue(patch.ActiveConfigurationId.IsSpecified);
        Assert.IsTrue(patch.AllowLan.IsSpecified);
        Assert.IsTrue(patch.SubscriptionRefreshHours.IsSpecified);
        Assert.IsFalse(patch.Theme.IsSpecified);
        Assert.AreEqual(proposed, patch.Apply(baseline));
    }
}
