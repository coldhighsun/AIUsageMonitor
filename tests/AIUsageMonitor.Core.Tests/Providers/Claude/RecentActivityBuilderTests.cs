using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

public class RecentActivityBuilderTests : IDisposable
{
    private readonly SessionParser _sessionParser = new(NullLogger<SessionParser>.Instance);
    private readonly RecentActivityBuilder _sut;
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid()}.jsonl");

    public RecentActivityBuilderTests()
    {
        _sut = new RecentActivityBuilder(new SessionFileCache(_sessionParser), new CostCalculator());
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    [Fact]
    public void Build_DuplicateMessageId_CountsTokensOnce()
    {
        var now = DateTimeOffset.Now;
        var recent = now.AddMinutes(-5);
        var timestamp = recent.ToString("O");

        var line = "{\"type\":\"assistant\",\"timestamp\":\"" + timestamp
            + "\",\"sessionId\":\"s1\",\"requestId\":\"req1\",\"message\":{\"id\":\"msg1\",\"role\":\"assistant\",\"model\":\"sonnet-5\",\"usage\":{\"input_tokens\":100,\"output_tokens\":50,\"cache_read_input_tokens\":10,\"cache_creation_input_tokens\":0}}}";

        File.WriteAllLines(_tempFile, [line, line]);

        var result = _sut.Build([_tempFile], TimeSpan.FromHours(1));

        Assert.Equal(160, result.TotalTokens);
        Assert.Equal(160, result.TokensByModel["sonnet-5"]);
    }
}
