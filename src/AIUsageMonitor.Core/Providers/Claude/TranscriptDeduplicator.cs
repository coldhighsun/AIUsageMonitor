using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Remembers which transcript lines and assistant responses have already been counted during one aggregation pass
/// over several transcript files. Resuming a session copies the earlier lines into the new transcript, and a
/// streamed response is written as several lines that repeat the same usage, so the same activity can show up
/// more than once, in one file or across files.
/// </summary>
internal sealed class TranscriptDeduplicator
{
    /// <summary>
    /// The identifiers of the lines that have already been seen.
    /// </summary>
    private readonly HashSet<string> _lineIds = [];

    /// <summary>
    /// The keys of the assistant responses whose usage has already been counted.
    /// </summary>
    private readonly HashSet<(string?, string?)> _usageKeys = [];

    /// <summary>
    /// Records a line unless an identical copy of it (same <c>uuid</c>) was seen before, e.g. in the transcript the
    /// session was resumed from. Lines without a <c>uuid</c> are never treated as copies.
    /// </summary>
    /// <param name="message">The transcript line.</param>
    /// <returns><see langword="true"/> if the line was new and should be counted; <see langword="false"/> if it repeats one already counted.</returns>
    public bool TryAddLine(SessionMessage message)
    {
        return message.Uuid is null || _lineIds.Add(message.Uuid);
    }

    /// <summary>
    /// Records the usage of an assistant line and reports whether the same response (same message id and request id)
    /// has already been counted. A response without ids is identified by its line <c>uuid</c>.
    /// </summary>
    /// <param name="message">The assistant line that carries usage.</param>
    /// <returns><see langword="true"/> if the usage has not been counted yet and should be added now.</returns>
    public bool TryAddUsage(SessionMessage message)
    {
        var key = (message.Message?.Id, message.RequestId) is (null, null)
            ? (message.Uuid, (string?)null)
            : (message.Message?.Id, message.RequestId);

        return _usageKeys.Add(key);
    }
}
