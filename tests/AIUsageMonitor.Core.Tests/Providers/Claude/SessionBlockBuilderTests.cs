using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Models;
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
    public void BuildCurrentSessionWindow_NoResetTime_SplitsOnIdleGapAndKeepsOnlyLatestBlock()
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

        var result = _sut.BuildCurrentSessionWindow([_tempFile], sessionResetAt: null);

        Assert.Equal(WindowConfidence.Estimated, result.Confidence);
        Assert.Equal(2, result.Messages);
        Assert.Equal(300, result.TotalTokens);
        Assert.Equal(newBlockStart, result.WindowStart);
        Assert.Equal(newBlockStart.AddHours(5), result.ResetsAt);
    }

    [Fact]
    public void BuildCurrentSessionWindow_WithFutureResetTime_PinsWindowAndOutranksLocalActivity()
    {
        // The reset time came from Claude's own UI, so while it is still in the future it wins
        // over anything inferred locally - here local activity would have placed the window's
        // start an hour earlier than the pinned window does.
        var now = DateTimeOffset.Now;
        var resetAt = now.AddHours(2);
        var expectedStart = resetAt.AddHours(-5);

        File.WriteAllLines(_tempFile,
        [
            BuildLine(now.AddHours(-4), "req-before-window"),
            BuildLine(now.AddHours(-1), "req-in-window"),
        ]);

        var result = _sut.BuildCurrentSessionWindow([_tempFile], resetAt);

        Assert.Equal(WindowConfidence.Confirmed, result.Confidence);
        Assert.Equal(expectedStart, result.WindowStart);
        Assert.Equal(resetAt, result.ResetsAt);
        Assert.Equal(1, result.Messages);
        Assert.Equal(150, result.TotalTokens);
    }

    [Fact]
    public void BuildCurrentSessionWindow_WithElapsedResetTimeAndRecentActivity_FallsBackToLocalEstimate()
    {
        // Once a reset time has elapsed it says nothing about the next window, so local activity
        // becomes the only evidence left.
        var now = DateTimeOffset.Now;
        var elapsedResetAt = now.AddHours(-1);
        var localBlockStart = now.AddHours(-2);

        File.WriteAllLines(_tempFile, [BuildLine(localBlockStart, "req-local")]);

        var result = _sut.BuildCurrentSessionWindow([_tempFile], elapsedResetAt);

        Assert.Equal(WindowConfidence.Estimated, result.Confidence);
        Assert.Equal(localBlockStart, result.WindowStart);
        Assert.Equal(localBlockStart.AddHours(5), result.ResetsAt);
        Assert.Equal(1, result.Messages);
    }

    [Fact]
    public void BuildCurrentSessionWindow_WithElapsedResetTimeAndStaleActivity_ReportsUnknown()
    {
        // Nothing left to go on: the reset time has elapsed and this machine hasn't been used in
        // over 5 hours. The account may be idle, or busy on another machine - so report neither.
        var now = DateTimeOffset.Now;

        File.WriteAllLines(_tempFile, [BuildLine(now.AddHours(-8), "req-stale")]);

        var result = _sut.BuildCurrentSessionWindow([_tempFile], now.AddHours(-1));

        Assert.Equal(WindowConfidence.Unknown, result.Confidence);
        Assert.Null(result.WindowStart);
        Assert.Null(result.ResetsAt);
        Assert.Equal(0, result.Messages);
        Assert.Equal(0, result.TotalTokens);
    }

    [Fact]
    public void BuildCurrentSessionWindow_NoResetTimeAndNoLocalActivity_ReportsUnknown()
    {
        File.WriteAllLines(_tempFile, Array.Empty<string>());

        var result = _sut.BuildCurrentSessionWindow([_tempFile], sessionResetAt: null);

        Assert.Equal(WindowConfidence.Unknown, result.Confidence);
        Assert.Null(result.ResetsAt);
    }

    [Fact]
    public void BuildCurrentSessionWindow_DuplicateMessageId_CountsTokensOnce()
    {
        var now = DateTimeOffset.Now;
        var line = BuildLine(now.AddMinutes(-5), "req1");
        File.WriteAllLines(_tempFile, [line, line]);

        var result = _sut.BuildCurrentSessionWindow([_tempFile], sessionResetAt: null);

        Assert.Equal(150, result.TotalTokens);
        Assert.Equal(150, result.TokensByModel["sonnet-5"]);
    }

    [Fact]
    public void BuildWeekWindow_NoAnchor_ReportsUnknownResetWithTrailingSevenDayUsage()
    {
        // The real weekly reset is a fixed time assigned to the account (not activity-driven), so
        // without a supplied reset time there's no way to derive it locally. The trailing 7 days
        // of usage is still shown as an upper bound on the current cycle's usage.
        var now = DateTimeOffset.Now;
        var earliest = now.AddDays(-3);

        File.WriteAllLines(_tempFile,
        [
            BuildLine(earliest, "req-early"),
            BuildLine(now.AddDays(-1), "req-late"),
        ]);

        var result = _sut.BuildWeekWindow([_tempFile], anchor: null);

        Assert.Equal(WindowConfidence.Unknown, result.Confidence);
        Assert.Null(result.ResetsAt);
        Assert.NotNull(result.WindowStart);
        Assert.True(Math.Abs((result.WindowStart!.Value - now.AddDays(-7)).TotalSeconds) < 5);
        Assert.Equal(2, result.Messages);
    }

    [Fact]
    public void BuildWeekWindow_NoAnchorAndNoMessages_ReportsUnknownWithNoUsage()
    {
        File.WriteAllLines(_tempFile, Array.Empty<string>());

        var result = _sut.BuildWeekWindow([_tempFile], anchor: null);

        Assert.Equal(WindowConfidence.Unknown, result.Confidence);
        Assert.Null(result.ResetsAt);
        Assert.Equal(0, result.Messages);
        Assert.Equal(0, result.TotalTokens);
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

        Assert.Equal(WindowConfidence.Confirmed, result.Confidence);
        Assert.Equal(expectedStart, result.WindowStart);
        Assert.Equal(expectedStart.AddDays(7), result.ResetsAt);
        Assert.Equal(1, result.Messages);
    }
}
