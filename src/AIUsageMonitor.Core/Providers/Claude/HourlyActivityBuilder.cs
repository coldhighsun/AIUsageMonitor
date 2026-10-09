using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Aggregates raw session transcripts into a token-by-hour-of-day distribution, since
/// <see cref="Models.StatsCache"/> only tracks message counts per hour, not tokens.
/// </summary>
public sealed class HourlyActivityBuilder(SessionFileCache sessionFileCache)
{
    /// <summary>
    /// Builds a list of <see cref="HourlyActivity"/> objects representing the total token usage for each hour of the day across all provided session files.
    /// </summary>
    /// <param name="sessionFiles">A list of session file paths to process.</param>
    /// <param name="progress">An optional progress reporter for tracking build progress (0-100).</param>
    /// <returns>A list of <see cref="HourlyActivity"/> objects.</returns>
    public List<HourlyActivity> Build(IReadOnlyList<string> sessionFiles, IProgress<int>? progress = null)
    {
        var tokensByHour = new long[24];
        var deduplicator = new TranscriptDeduplicator();

        sessionFileCache.WarmUp(sessionFiles, progress);

        foreach (var file in sessionFiles)
        {
            ProcessFile(file, tokensByHour, deduplicator);
        }

        return Enumerable.Range(0, 24)
            .Select(h => new HourlyActivity(h, tokensByHour[h]))
            .ToList();
    }

    /// <summary>
    /// Processes a single session file, updating the provided tokensByHour array with the total token usage for each hour of the day.
    /// </summary>
    /// <param name="file">The path to the session file to process.</param>
    /// <param name="tokensByHour">An array representing the total token usage for each hour of the day.</param>
    /// <param name="deduplicator">Remembers the responses already counted, across all files of this pass.</param>
    private void ProcessFile(string file, long[] tokensByHour, TranscriptDeduplicator deduplicator)
    {
        IReadOnlyList<Models.SessionMessage> parsed;
        try
        {
            parsed = sessionFileCache.GetRows(file);
        }
        catch
        {
            return;
        }

        foreach (var msg in parsed)
        {
            if (msg.Type != "assistant" ||
                msg.Timestamp is not { } ts)
            {
                continue;
            }

            var usage = msg.Message?.Usage;
            if (usage is null)
            {
                continue;
            }

            var model = msg.Message?.Model ?? "unknown";
            if (model == "<synthetic>")
            {
                continue;
            }

            if (!deduplicator.TryAddUsage(msg))
            {
                continue;
            }

            var tokens =
                usage.InputTokens +
                usage.OutputTokens +
                usage.CacheReadInputTokens +
                usage.CacheCreationInputTokens;

            tokensByHour[ts.LocalDateTime.Hour] += tokens;
        }
    }
}