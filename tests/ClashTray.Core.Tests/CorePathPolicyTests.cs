using System.Diagnostics;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class CorePathPolicyTests
{
    [TestMethod]
    public void IsManagedCorePathAcceptsManagedExecutableRegardlessOfCaseAndSeparators()
    {
        string root = CreateRoot();
        try
        {
            AppPaths paths = CreatePaths(root);
            Assert.IsTrue(CorePathPolicy.IsManagedCorePath(paths, paths.ManagedCoreExecutable));
            Assert.IsTrue(CorePathPolicy.IsManagedCorePath(paths, paths.ManagedCoreExecutable.ToUpperInvariant()));
            Assert.IsTrue(CorePathPolicy.IsManagedCorePath(
                paths,
                Path.Combine(paths.CoreRoot, ".", "mihomo.exe")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void IsManagedCorePathRejectsUnrelatedPaths()
    {
        string root = CreateRoot();
        try
        {
            AppPaths paths = CreatePaths(root);
            Assert.IsFalse(CorePathPolicy.IsManagedCorePath(
                paths,
                Path.Combine(paths.CoreRoot, "other.exe")));
            Assert.IsFalse(CorePathPolicy.IsManagedCorePath(
                paths,
                Path.Combine(paths.ProgramRoot, "mihomo.exe")));
            Assert.IsFalse(CorePathPolicy.IsManagedCorePath(paths, root));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void IsManagedCorePathRejectsInvalidPaths()
    {
        string root = CreateRoot();
        try
        {
            AppPaths paths = CreatePaths(root);
            Assert.IsFalse(CorePathPolicy.IsManagedCorePath(paths, "mihomo\0.exe"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void IsManagedCorePathRejectsJunctionedCoreDirectory()
    {
        string root = CreateRoot();
        try
        {
            AppPaths paths = CreatePaths(root);
            Assert.IsTrue(CorePathPolicy.IsManagedCorePath(paths, paths.ManagedCoreExecutable));

            string realCore = Path.Combine(root, "real-core");
            Directory.CreateDirectory(realCore);
            CreateJunction(paths.CoreRoot, realCore);

            Assert.IsFalse(CorePathPolicy.IsManagedCorePath(paths, paths.ManagedCoreExecutable));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void IsManagedRuntimeDirectoryAcceptsOnlyTheExactManagedDirectory()
    {
        string root = CreateRoot();
        try
        {
            AppPaths paths = CreatePaths(root);
            string managed = Path.Combine(paths.RuntimeRoot, "mihomo");
            Assert.IsTrue(CorePathPolicy.IsManagedRuntimeDirectory(paths, managed));
            Assert.IsTrue(CorePathPolicy.IsManagedRuntimeDirectory(paths, managed + Path.DirectorySeparatorChar));
            Assert.IsFalse(CorePathPolicy.IsManagedRuntimeDirectory(paths, paths.RuntimeRoot));
            Assert.IsFalse(CorePathPolicy.IsManagedRuntimeDirectory(
                paths,
                Path.Combine(paths.RuntimeRoot, "other")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void IsManagedRuntimeFileRequiresAFileInsideTheManagedDirectory()
    {
        string root = CreateRoot();
        try
        {
            AppPaths paths = CreatePaths(root);
            Assert.IsTrue(CorePathPolicy.IsManagedRuntimeFile(
                paths,
                Path.Combine(paths.RuntimeRoot, "mihomo", "config.yaml")));
            Assert.IsFalse(CorePathPolicy.IsManagedRuntimeFile(
                paths,
                Path.Combine(paths.RuntimeRoot, "config.yaml")));
            Assert.IsFalse(CorePathPolicy.IsManagedRuntimeFile(
                paths,
                Path.Combine(paths.ProgramRoot, "config.yaml")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void HasReparsePointOnPathDetectsJunctionBetweenTargetAndBoundary()
    {
        string root = CreateRoot();
        try
        {
            AppPaths paths = CreatePaths(root);
            Directory.CreateDirectory(paths.CoreRoot);
            Assert.IsFalse(CorePathPolicy.HasReparsePointOnPath(paths.ManagedCoreExecutable, paths.ProgramRoot));

            string realCore = Path.Combine(root, "real-core");
            Directory.CreateDirectory(realCore);
            Directory.Delete(paths.CoreRoot);
            CreateJunction(paths.CoreRoot, realCore);

            Assert.IsTrue(CorePathPolicy.HasReparsePointOnPath(paths.ManagedCoreExecutable, paths.ProgramRoot));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void HasReparsePointOnPathIgnoresJunctionAboveTheBoundary()
    {
        string root = CreateRoot();
        try
        {
            string realRoot = Path.Combine(root, "real-root");
            string junctionRoot = Path.Combine(root, "junction-root");
            Directory.CreateDirectory(realRoot);
            CreateJunction(junctionRoot, realRoot);

            // ProgramRoot itself is reached through a junction, but the boundary
            // comparison walks from the target up to (and including) ProgramRoot
            // only, so a junction above the boundary must not taint the result.
            AppPaths paths = new(
                Path.Combine(junctionRoot, "local"),
                Path.Combine(junctionRoot, "program"));
            Directory.CreateDirectory(paths.CoreRoot);
            Assert.IsFalse(CorePathPolicy.HasReparsePointOnPath(paths.ManagedCoreExecutable, paths.ProgramRoot));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void HasReparsePointOnPathTreatsUnreachableBoundaryAsUnsafe()
    {
        // The boundary is on another drive tree, so the walk reaches the file
        // system root without ever meeting it; that must fail closed.
        Assert.IsTrue(CorePathPolicy.HasReparsePointOnPath(
            Path.Combine(Path.GetTempPath(), "clashTray-boundary-check", "core", "mihomo.exe"),
            @"D:\clashTray-never-a-parent"));
    }

    private static AppPaths CreatePaths(string root)
    {
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        return paths;
    }

    private static void CreateJunction(string link, string target)
    {
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("cmd.exe is unavailable for junction creation.");
        Assert.IsTrue(process.WaitForExit(10_000), "mklink /J did not finish in time.");
        Assert.AreEqual(0, process.ExitCode, "mklink /J failed.");
    }

    private static string CreateRoot() =>
        Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        // Recursive delete fails on reparse points; unlink junctions first.
        DeleteJunctions(root);
        Directory.Delete(root, recursive: true);
    }

    private static void DeleteJunctions(string directory)
    {
        foreach (string entry in Directory.EnumerateDirectories(directory))
        {
            if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(entry);
            }
            else
            {
                DeleteJunctions(entry);
            }
        }
    }
}
