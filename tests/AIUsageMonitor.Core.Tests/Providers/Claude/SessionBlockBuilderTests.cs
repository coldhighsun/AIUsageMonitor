using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

public class SessionBlockBuilderTests : IDisposable
{
    private readonly SessionParser _sessionParser = new(NullLogger<SessionParser>.Instance);
    private readonly SessionBlockBuilder _sut;
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid()}.jsonl");

    public SessionBlockBuilderTests()
    {
        _sut = new SessionBlockBuilder(new SessionFileCache(_sessionParser, NullLogger<SessionFileCache>.Instance), new CostCalculator());
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    private static string BuildLine(DateTimeOffset timestamp, string requestId, long inputTokens = 100, long outputTokens = 50)
        => "{\"type\":\"assistant\",\"timestamp\":\"" + timestamp.ToString("O")
           + "\",\"sessionId\":\"s1\",\"requestId\":\"" + requestId
           + "\",\"message\":{\"id\":\"msg-" + requestId + "\",\"role\":\"assistant\",\"model\":\"sonnet-5\","
           + "\"usage\":{\"input_tokens\":" + inputTokens + ",\"output_tokens\":" + outputTokens
           + ",\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}";

    [Fact]
    public void BuildCurrentSessionWindow_NoAnchor_SplitsOnIdleGapAndKeepsOnlyLatestBlock()
    {
        var now = DateTimeOffset.Now;
        var oldBlockStart = now.AddHours(-8);
        var newBlockStart = now.AddHours(-1);

        File.WriteAllLines(_tempFile,
        [
            BuildLine(oldBlockStart, "req-old"),
            BuildLine(newBlockStart, "req-new"),
            BuildLine(now.AddMinutes(-5), "req-new2"),
        ]);

        var result = _sut.BuildCurrentSessionWindow([_tempFile], anchor: null);

        Assert.True(result.IsAnchorEstimated);
        Assert.Equal(2, result.Messages);
        Assert.Equal(300, result.TotalTokens);
        Assert.Equal(newBlockStart, result.WindowStart);
        Assert.Equal(newBlockStart.AddHours(5), result.ResetsAt);
    }

    [Fact]
    public void BuildCurrentSessionWindow_WithAnchor_PinsWindowAndOnlyCountsMessagesWithin()
    {
        var anchor = DateTimeOffset.Now.AddHours(-2);
        var outsideWindow = anchor.AddHours(6);

        File.WriteAllLines(_tempFile,
        [
            BuildLine(anchor.AddMinutes(10), "req-in"),
            BuildLine(outsideWindow, "req-out"),
        ]);

        var result = _sut.BuildCurrentSessionWindow([_tempFile], anchor);

        Assert.False(result.IsAnchorEstimated);
        Assert.Equal(anchor, result.WindowStart);
        Assert.Equal(anchor.AddHours(5), result.ResetsAt);
        Assert.Equal(1, result.Messages);
        Assert.Equal(150, result.TotalTokens);
    }

    [Fact]
    public void BuildCurrentSessionWindow_WithStaleAnchor_RollsForwardToCurrentPeriod()
    {
        // Anchor was seen 13 hours ago (2 full 5h periods + 3h ago); the current period should
        // start 2 periods later, i.e. 3 hours ago, not stay pinned to the original anchor.
        var now = DateTimeOffset.Now;
        var staleAnchor = now.AddHours(-13);
        var expectedStart = staleAnchor.AddHours(10);

        File.WriteAllLines(_tempFile, [BuildLine(expectedStart.AddMinutes(5), "req-current")]);

        var result = _sut.BuildCurrentSessionWindow([_tempFile], staleAnchor);

        Assert.False(result.IsAnchorEstimated);
        Assert.Equal(expectedStart, result.WindowStart);
        Assert.Equal(expectedStart.AddHours(5), result.ResetsAt);
        Assert.Equal(1, result.Messages);
    }

    [Fact]
    public void BuildCurrentSessionWindow_DuplicateMessageId_CountsTokensOnce()
    {
        var now = DateTimeOffset.Now;
        var line = BuildLine(now.AddMinutes(-5), "req1");
        File.WriteAllLines(_tempFile, [line, line]);

        var result = _sut.BuildCurrentSessionWindow([_tempFile], anchor: null);

        Assert.Equal(150, result.TotalTokens);
        Assert.Equal(150, result.TokensByModel["sonnet-5"]);
    }

    [Fact]
    public void BuildWeekWindow_NoAnchor_UsesRollingSevenDayWindow()
    {
        var now = DateTimeOffset.Now;
        var earliest = now.AddDays(-3);

        File.WriteAllLines(_tempFile,
        [
            BuildLine(earliest, "req-early"),
            BuildLine(now.AddDays(-1), "req-late"),
        ]);

        var result = _sut.BuildWeekWindow([_tempFile], anchor: null);

        Assert.True(result.IsAnchorEstimated);
        Assert.Equal(2, result.Messages);
        Assert.Equal(earliest.AddDays(7), result.ResetsAt);
    }

    [Fact]
    public void BuildWeekWindow_WithAnchor_PinsWindowToMostRecentAnchorOccurrence()
    {
        var now = DateTimeOffset.Now;
        var anchor = (now.DayOfWeek, TimeSpan.FromHours(9));

        var expectedStart = new DateTimeOffset(now.Year, now.Month, now.Day, 9, 0, 0, now.Offset);
        if (expectedStart > now)
        {
            expectedStart = expectedStart.AddDays(-7);
        }

        File.WriteAllLines(_tempFile, [BuildLine(expectedStart.AddMinutes(5), "req-in")]);

        var result = _sut.BuildWeekWindow([_tempFile], anchor);

        Assert.False(result.IsAnchorEstimated);
        Assert.Equal(expectedStart, result.WindowStart);
        Assert.Equal(expectedStart.AddDays(7), result.ResetsAt);
        Assert.Equal(1, result.Messages);
    }
}
