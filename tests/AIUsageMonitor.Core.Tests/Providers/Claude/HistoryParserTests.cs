using AIUsageMonitor.Core.Providers.Claude;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

public class HistoryParserTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid()}.jsonl");
    private readonly HistoryParser _sut = new();

    [Fact]
    public void Parse_MissingFile_ReturnsEmpty()
    {
        var entries = _sut.Parse(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.jsonl"));

        Assert.Empty(entries);
    }

    [Fact]
    public void Parse_SkipsBlankLinesAndMalformedJson()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"display":"hello","timestamp":1000,"project":"proj","sessionId":"s1"}""",
            "",
            "   ",
            "not-json{{{",
            """{"display":"world","timestamp":2000}""",
        ]);

        var entries = _sut.Parse(_tempFile).ToList();

        Assert.Equal(2, entries.Count);
        Assert.Equal("hello", entries[0].Display);
        Assert.Equal(1000, entries[0].Timestamp);
        Assert.Equal("proj", entries[0].Project);
        Assert.Equal("world", entries[1].Display);
        Assert.Null(entries[1].Project);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }
}
