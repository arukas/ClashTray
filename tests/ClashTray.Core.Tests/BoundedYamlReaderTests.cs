using System.Text;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class BoundedYamlReaderTests
{
    [TestMethod]
    public async Task GrowthDuringReadIsBoundedByActualBytesRatherThanInitialLength()
    {
        using GrowAfterReadStream source = new();
        Assert.IsTrue(source.Length < RuntimeConfigBuilder.MaximumInputBytes);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => BoundedYamlReader.ReadAsync(source, CancellationToken.None));
        Assert.IsTrue(source.Position <= RuntimeConfigBuilder.MaximumInputBytes + 1L);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task DecodedLineAndLineCountLimitsAreEnforced(int kind)
    {
        string text = kind == 0 ? new string('a', RuntimeConfigBuilder.MaximumLineCharacters + 1)
            : new string('\n', RuntimeConfigBuilder.MaximumInputLines + 1);
        using MemoryStream source = new(Encoding.UTF8.GetBytes(text));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => BoundedYamlReader.ReadAsync(source, CancellationToken.None));
    }

    [TestMethod]
    public async Task BomAndNewlineBehaviorMatchReadAllLinesAndCancellationIsNotSwallowed()
    {
        string text = "mode: direct\r\n# comment\r\nlast\n";
        using MemoryStream source = new([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)]);
        string[] result = await BoundedYamlReader.ReadAsync(source, CancellationToken.None);
        string[] expected = ["mode: direct", "# comment", "last"];
        CollectionAssert.AreEqual(expected, result);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        using MemoryStream canceled = new(Encoding.UTF8.GetBytes(text));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => BoundedYamlReader.ReadAsync(canceled, cancellation.Token));
    }

    private sealed class GrowAfterReadStream : MemoryStream
    {
        private bool _grown;
        public GrowAfterReadStream() { Write(Encoding.UTF8.GetBytes("mode: direct\n")); Position = 0; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer, cancellationToken);
            if (!_grown)
            {
                _grown = true;
                SetLength(RuntimeConfigBuilder.MaximumInputBytes + 1L);
            }
            return read;
        }
    }
}
