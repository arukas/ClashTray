using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeConfigBuilderResourceTests
{
    private const string LastGood = "last-successful-output";

    [TestMethod]
    public async Task InputByteLimitRejectsSparseOversizedSourceBeforeReplacingOutput()
    {
        string root = CreateRoot();
        string sourcePath = Path.Combine(root, "source.yaml");
        string destinationPath = Path.Combine(root, "effective.yaml");
        try
        {
            using (FileStream source = new(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.SetLength(RuntimeConfigBuilder.MaximumInputBytes + 1L);
            }

            await File.WriteAllTextAsync(destinationPath, LastGood);
            InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                RuntimeConfigBuilder.BuildAsync(sourcePath, destinationPath, new AppSettings()));

            StringAssert.Contains(exception.Message, "byte runtime builder limit", StringComparison.Ordinal);
            Assert.AreEqual(LastGood, await File.ReadAllTextAsync(destinationPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InputLineCountLimitRejectsSourceBeforeReplacingOutput()
    {
        string root = CreateRoot();
        string sourcePath = Path.Combine(root, "source.yaml");
        string destinationPath = Path.Combine(root, "effective.yaml");
        try
        {
            string source = string.Join(
                Environment.NewLine,
                Enumerable.Repeat("mode: direct", RuntimeConfigBuilder.MaximumInputLines + 1));
            await File.WriteAllTextAsync(sourcePath, source);
            await File.WriteAllTextAsync(destinationPath, LastGood);

            InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                RuntimeConfigBuilder.BuildAsync(sourcePath, destinationPath, new AppSettings()));

            StringAssert.Contains(exception.Message, "line runtime builder limit", StringComparison.Ordinal);
            Assert.AreEqual(LastGood, await File.ReadAllTextAsync(destinationPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task OverlongLineIsRejectedBeforeReplacingOutput()
    {
        string root = CreateRoot();
        string sourcePath = Path.Combine(root, "source.yaml");
        string destinationPath = Path.Combine(root, "effective.yaml");
        try
        {
            string source = "mode: direct" + Environment.NewLine
                + "secret: " + new string('x', RuntimeConfigBuilder.MaximumLineCharacters);
            await File.WriteAllTextAsync(sourcePath, source);
            await File.WriteAllTextAsync(destinationPath, LastGood);

            InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                RuntimeConfigBuilder.BuildAsync(sourcePath, destinationPath, new AppSettings()));

            StringAssert.Contains(exception.Message, "character runtime builder limit", StringComparison.Ordinal);
            Assert.AreEqual(LastGood, await File.ReadAllTextAsync(destinationPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task PreCancelledBuildKeepsLastSuccessfulOutputAndSource()
    {
        string root = CreateRoot();
        string sourcePath = Path.Combine(root, "source.yaml");
        string destinationPath = Path.Combine(root, "effective.yaml");
        string source = "mode: direct" + Environment.NewLine + "secret: stale";
        try
        {
            await File.WriteAllTextAsync(sourcePath, source);
            await File.WriteAllTextAsync(destinationPath, LastGood);
            using CancellationTokenSource cancellation = new();
            await cancellation.CancelAsync();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                RuntimeConfigBuilder.BuildAsync(sourcePath, destinationPath, new AppSettings(), cancellation.Token));

            Assert.AreEqual(source, await File.ReadAllTextAsync(sourcePath));
            Assert.AreEqual(LastGood, await File.ReadAllTextAsync(destinationPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task QuotedScalarScannerObservesCancellationDuringLongLinearScan()
    {
        string firstLine = "secret: \"" + new string('a', RuntimeConfigBuilder.MaximumLineCharacters - 16);
        string continuation = "  " + new string('b', RuntimeConfigBuilder.MaximumLineCharacters - 2);
        string[] lines = new string[200];
        lines[0] = firstLine;
        Array.Fill(lines, continuation, 1, lines.Length - 1);

        using CancellationTokenSource cancellation = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> scan = Task.Run(() =>
        {
            started.SetResult();
            return RuntimeConfigBuilder.FindQuotedScalarEnd(
                lines,
                firstLine: 0,
                parentIndent: 0,
                quote: '"',
                cancellation.Token);
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(2));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => scan);
    }

    [TestMethod]
    public async Task UnclosedQuotedManagedScalarKeepsLastSuccessfulOutput()
    {
        string root = CreateRoot();
        string sourcePath = Path.Combine(root, "source.yaml");
        string destinationPath = Path.Combine(root, "effective.yaml");
        string source = "mode: direct" + Environment.NewLine
            + "secret: \"unclosed" + Environment.NewLine
            + "  continuation";
        try
        {
            await File.WriteAllTextAsync(sourcePath, source);
            await File.WriteAllTextAsync(destinationPath, LastGood);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                RuntimeConfigBuilder.BuildAsync(sourcePath, destinationPath, new AppSettings()));

            Assert.AreEqual(source, await File.ReadAllTextAsync(sourcePath));
            Assert.AreEqual(LastGood, await File.ReadAllTextAsync(destinationPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
