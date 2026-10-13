namespace ClashTray.Core.Tests;

internal static class TestDirectoryCleanup
{
    internal static Task DeleteAsync(string path, Action? sharingViolationObserved = null) =>
        ClashTray.Testing.TestFixtureDirectory.DeleteAsync(path, sharingViolationObserved);
}
