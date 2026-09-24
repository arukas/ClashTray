namespace ClashTray.Core.Tests;

[TestClass]
public sealed class MihomoOutputLineReaderTests
{
    [TestMethod]
    public async Task DifferentReadChunkingProducesTheSameCompleteLogSequence()
    {
        const string text = "first\r\nsecond\nthird\r\nfourth-at-eof";
        string[] expected = ["first", "second", "third", "fourth-at-eof"];
        int[][] chunkPatterns =
        [
            [1024],
            [1],
            [2, 3, 1, 7],
            [5, 8, 2]
        ];

        foreach (int[] pattern in chunkPatterns)
        {
            using ChunkedTextReader reader = new(text, pattern);
            (string Line, bool IsError)[] actual = await DrainAsync(reader);
            CollectionAssert.AreEqual(expected, actual.Select(item => item.Line).ToArray());
            Assert.IsTrue(actual.All(item => item.IsError));
        }
    }

    [TestMethod]
    public async Task TruncatingAnOversizedLineDoesNotConsumeTheFollowingLine()
    {
        const int maximumLineCharacters = 64 * 1024;
        string longLine = new string('L', maximumLineCharacters + 2048);
        using ChunkedTextReader reader = new(longLine + "\r\nnormal-after-long-line\n", [1024]);
        (string Line, bool IsError)[] actual = await DrainAsync(reader);

        Assert.AreEqual(2, actual.Length);
        Assert.AreEqual(
            new string('L', maximumLineCharacters) + " … [日志行已截断]",
            actual[0].Line);
        Assert.AreEqual("normal-after-long-line", actual[1].Line);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2025",
        Justification = "The drain task is awaited with a timeout before the cancellation reader is disposed.")]
    public async Task CancellationDiscardsAnUnterminatedPartialLineWithoutHanging()
    {
        using CancellationTokenSource cancellation = new();
        using PartialThenBlockingTextReader reader = new("partial-output");
        List<string> lines = [];
        Task drain = MihomoProcessManager.DrainOutputAsync(
            reader,
            isError: false,
            (line, _) => lines.Add(line),
            cancellation.Token);

        await reader.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();
        await drain.WaitAsync(TimeSpan.FromSeconds(2));
        await drain;

        Assert.AreEqual(0, lines.Count);
    }

    private static async Task<(string Line, bool IsError)[]> DrainAsync(TextReader reader)
    {
        List<(string Line, bool IsError)> lines = [];
        await MihomoProcessManager.DrainOutputAsync(
            reader,
            isError: true,
            (line, isError) => lines.Add((line, isError)),
            CancellationToken.None);
        return lines.ToArray();
    }

    private sealed class ChunkedTextReader(string text, int[] chunkPattern) : TextReader
    {
        private int _position;
        private int _readCount;

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position >= text.Length || buffer.IsEmpty)
            {
                return ValueTask.FromResult(0);
            }

            int maximum = chunkPattern[_readCount++ % chunkPattern.Length];
            int count = Math.Min(Math.Min(maximum, buffer.Length), text.Length - _position);
            text.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class PartialThenBlockingTextReader(string partial) : TextReader
    {
        private bool _returnedPartial;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (!_returnedPartial)
            {
                _returnedPartial = true;
                partial.AsMemory().CopyTo(buffer);
                return partial.Length;
            }

            Blocked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}