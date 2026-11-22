using ClashTray.App;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ThemeApplicationStateTests
{
    [TestMethod]
    public void FirstThemeAlwaysApplies()
    {
        ThemeApplicationState state = new ThemeApplicationState();

        Assert.AreEqual("system", state.GetThemeToApply("system", false));
    }

    [TestMethod]
    public void RepeatedThemeAndUnlockStateDoesNotApply()
    {
        ThemeApplicationState state = new ThemeApplicationState();
        Assert.AreEqual("dark", state.GetThemeToApply("dark", false));

        Assert.IsNull(state.GetThemeToApply("dark", false));
    }

    [TestMethod]
    public void ThemeChangesApplyAndNormalize()
    {
        ThemeApplicationState state = new ThemeApplicationState();
        state.GetThemeToApply("system", false);

        Assert.AreEqual("dark", state.GetThemeToApply("  Dark  ", false));
        Assert.AreEqual("light", state.GetThemeToApply("light", false));
    }

    [TestMethod]
    public void LockedNakhimovFallsBackToSystemOnce()
    {
        ThemeApplicationState state = new ThemeApplicationState();

        Assert.AreEqual("system", state.GetThemeToApply("nakhimov", false));
        Assert.IsNull(state.GetThemeToApply("nakhimov", false));
    }

    [TestMethod]
    public void UnlockStateChangeAppliesEvenWhenThemeIsUnchanged()
    {
        ThemeApplicationState state = new ThemeApplicationState();
        state.GetThemeToApply("dark", false);

        Assert.AreEqual("dark", state.GetThemeToApply("dark", true));
        Assert.IsNull(state.GetThemeToApply("dark", true));
    }
}
