using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Parses session messages from a file and provides methods to analyze the session, such as calculating the total number of tokens used and the duration of the session.
/// </summary>
/// <param name="logger">The logger instance used for logging warnings and errors during parsing.</param>
public sealed class SessionParser(ILogger<SessionParser> logger)
{
    /// <summary>
    /// Parses a file containing session messages in JSON format and yields each message as a <see cref="SessionMessage"/> object.
    /// </summary>
    /// <param name="filePath">The path to the file containing the session messages.</param>
    /// <returns>An enumerable of <see cref="SessionMessage"/> objects parsed from the file.</returns>
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

    /// <summary>
    /// Parses a file containing session messages and returns a summary of the session, including the session ID, project, start and end times, duration, message count, total tokens used, and tokens used by model.
    /// </summary>
    /// <param name="filePath">The path to the file containing the session messages.</param>
    /// <returns>A <see cref="SessionSummary"/> object containing the summary of the session, or <c>null</c> if no messages were found.</returns>
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
            .Where(m => m is { Type: "assistant", Message.Usage: not null })
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