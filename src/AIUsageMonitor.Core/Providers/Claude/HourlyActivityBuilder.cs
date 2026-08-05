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

        for (var fileIndex = 0; fileIndex < sessionFiles.Count; fileIndex++)
        {
            try
            {
                ProcessFile(sessionFiles[fileIndex], tokensByHour);
            }
            finally
            {
                progress?.Report((fileIndex + 1) * 100 / sessionFiles.Count);
            }
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
    private void ProcessFile(string file, long[] tokensByHour)
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
                msg.Timestamp is null ||
                !DateTimeOffset.TryParse(msg.Timestamp, out var ts))
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

            var tokens =
                usage.InputTokens +
                usage.OutputTokens +
                usage.CacheReadInputTokens +
                usage.CacheCreationInputTokens;

            tokensByHour[ts.LocalDateTime.Hour] += tokens;
        }
    }
}