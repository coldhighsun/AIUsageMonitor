using System.Text.Json;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Core.Providers.Claude;

public sealed class SessionParser(ILogger<SessionParser> logger)
{
    public IEnumerable<SessionMessage> ParseFile(string filePath)
    {
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            SessionMessage? msg;
            try
            {
                msg = JsonSerializer.Deserialize(line, CoreJsonContext.Default.SessionMessage);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to parse line in {File}", filePath);
                continue;
            }

            if (msg is not null)
            {
                yield return msg;
            }
        }
    }

    public SessionSummary? ParseSessionSummary(string filePath)
    {
        var messages = ParseFile(filePath).ToList();
        if (messages.Count == 0)
        {
            return null;
        }

        var sessionId = messages.FirstOrDefault(m => m.SessionId is not null)?.SessionId
            ?? Path.GetFileNameWithoutExtension(filePath);
        var project = messages.FirstOrDefault(m => m.Cwd is not null)?.Cwd;

        var timestamps = messages
            .Where(m => m.Timestamp is not null)
            .Select(m => DateTimeOffset.Parse(m.Timestamp!))
            .OrderBy(t => t)
            .ToList();

        if (timestamps.Count == 0)
        {
            return null;
        }

        var startTime = timestamps[0];
        var endTime = timestamps[^1];

        var assistantMessages = messages
            .Where(m => m.Type == "assistant" && m.Message?.Usage is not null)
            .ToList();

        long totalTokens = 0;
        var tokensByModel = new Dictionary<string, long>();

        foreach (var msg in assistantMessages)
        {
            var usage = msg.Message!.Usage!;
            var msgTokens = usage.InputTokens + usage.OutputTokens
                + usage.CacheReadInputTokens + usage.CacheCreationInputTokens;
            totalTokens += msgTokens;

            var model = msg.Message.Model ?? "unknown";
            tokensByModel[model] = tokensByModel.GetValueOrDefault(model) + msgTokens;
        }

        return new(
            sessionId,
            project,
            startTime,
            endTime,
            endTime - startTime,
            messages.Count(m => m.Type is "user" or "assistant"),
            totalTokens,
            tokensByModel);
    }
}
