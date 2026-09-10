using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ThemeUnlockTests
{
    private static readonly int[] ExpectedRemaining = [4, 3, 2, 1, 0, 4];
    [TestMethod]
    public void OnlyFifthConsecutiveClickUnlocks()
    {
        LogoUnlockSequence sequence = new LogoUnlockSequence();
        CollectionAssert.AreEqual(ExpectedRemaining,
            Enumerable.Range(0, 6).Select(index => sequence.Click(index * 200)).ToArray());
    }

    [TestMethod]
    public void PauseOrPanelCloseDiscardsPartialSequence()
    {
        LogoUnlockSequence sequence = new LogoUnlockSequence();
        for (int index = 0; index < 4; index++)
        {
            sequence.Click(index * 100);
        }

        Assert.AreEqual(4, sequence.Click(1801));
        sequence.Click(1900);
        sequence.Reset();
        Assert.AreEqual(4, sequence.Click(2000));
    }

    [TestMethod]
    public async Task UnlockAndSelectionSurviveReloadAndSwitchingBack()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        SettingsStore store = new SettingsStore(paths);
        try
        {
            Assert.IsFalse((await store.LoadAsync()).NakhimovUnlocked);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync(new AppSettings(Theme: "nakhimov")));
            AppSettings unlocked = new AppSettings(Theme: "nakhimov", NakhimovUnlocked: true);
            await store.SaveAsync(unlocked);
            Assert.AreEqual(unlocked, await new SettingsStore(paths).LoadAsync());
            await store.SaveAsync(unlocked with { Theme = "light" });
            AppSettings switchedBack = await new SettingsStore(paths).LoadAsync();
            Assert.AreEqual("light", switchedBack.Theme);
            Assert.IsTrue(switchedBack.NakhimovUnlocked);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
