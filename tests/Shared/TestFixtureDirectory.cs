using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ClashTray.Testing;

internal static class TestFixtureDirectory
{
    private static readonly ConcurrentDictionary<string, byte> Owned = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string Parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ClashTrayTests"));

    public static string Create()
    {
        ValidateAncestors(Parent);
        string root = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Owned.TryAdd(root, 0);
        Console.WriteLine($"Test fixture: {root}");
        return root;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Report both body and cleanup failures with their original stacks.")]
    public static async Task RunAsync(string root, Func<Task> body, Func<Task>? cleanup = null)
    {
        Exception? primary = null;
        try { await body().ConfigureAwait(false); }
        catch (Exception exception) { primary = exception; }
        try { await (cleanup?.Invoke() ?? DeleteAsync(root)).ConfigureAwait(false); }
        catch (Exception exception)
        {
            if (primary is not null) { throw new AggregateException("Test body and fixture cleanup both failed.", primary, exception); }
            throw;
        }
        if (primary is not null) { ExceptionDispatchInfo.Capture(primary).Throw(); }
    }

    public static void Run(string root, Action body) => RunAsync(root, () => { body(); return Task.CompletedTask; }).GetAwaiter().GetResult();

    public static async Task DeleteAsync(string path, Action? sharingViolationObserved = null, Action<string>? beforeEntryInspection = null)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(Path.GetDirectoryName(root), Parent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(root), "N", out _))
        {
            throw new InvalidOperationException("Cleanup is restricted to a direct GUID test fixture directory.");
        }
        ValidateAncestors(root);
        if (!Directory.Exists(root)) { return; }
        if (!Owned.ContainsKey(root)) { throw new InvalidOperationException("Cleanup requires a fixture created by this test process."); }
        RestoreOwnedTree(root, root, 0, beforeEntryInspection);
        long start = Stopwatch.GetTimestamp();
        while (Directory.Exists(root))
        {
            ValidateAncestors(root);
            try { Directory.Delete(root, recursive: true); }
            catch (IOException exception) when ((exception.HResult & 0xffff) == 32 && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(2))
            {
                sharingViolationObserved?.Invoke();
                await Task.Delay(TimeSpan.FromMilliseconds(40)).ConfigureAwait(false);
            }
        }
        Owned.TryRemove(root, out _);
    }

    private static void ValidateAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            { throw new InvalidOperationException("A fixture path must not traverse a reparse point."); }
        }
    }

    private static void RestoreOwnedTree(string root, string path, int depth, Action<string>? beforeEntryInspection)
    {
        try { RestoreOwnedEntry(root, path, depth, beforeEntryInspection); }
        // Atomic writers can rename/remove a listed temp file before inspection.
        // Absence already satisfies cleanup; ACL, ownership, reparse and other
        // I/O failures remain errors and are never treated as disappearance.
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void RestoreOwnedEntry(string root, string path, int depth, Action<string>? beforeEntryInspection)
    {
        if (depth > 64 || !Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && path != root)
        { throw new InvalidOperationException("Fixture cleanup escaped its owned root or exceeded depth limits."); }
        beforeEntryInspection?.Invoke(path);
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) { throw new InvalidOperationException("Fixture cleanup refuses reparse points."); }
        bool directory = (attributes & FileAttributes.Directory) != 0;
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier sid = identity.User ?? throw new InvalidOperationException("Fixture owner SID is unavailable.");
        FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        if (!sid.Equals(security.GetOwner(typeof(SecurityIdentifier))))
        { throw new InvalidOperationException("Fixture cleanup refuses to modify a different owner's ACL."); }
        security.SetAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
            PropagationFlags.None, AccessControlType.Allow));
        if (directory) { new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security); }
        else { new FileInfo(path).SetAccessControl((FileSecurity)security); }
        if ((attributes & FileAttributes.ReadOnly) != 0) { File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly); }
        if (directory)
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(path)) { RestoreOwnedTree(root, child, depth + 1, beforeEntryInspection); }
        }
    }
}
