using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Text.Json;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Parses session messages from a file and provides methods to analyze the session, such as calculating the total number of tokens used and the duration of the session.
/// </summary>
/// <param name="logger">The logger instance used for logging warnings and errors during parsing.</param>
public sealed class SessionParser(ILogger<SessionParser> logger)
{
    /// <summary>
    /// The initial size of the line buffer; it doubles whenever a single line does not fit.
    /// </summary>
    private const int InitialBufferSize = 256 * 1024;

    /// <summary>
    /// The UTF-8 byte-order mark that may prefix the first line of a file.
    /// </summary>
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Parses a file containing session messages in JSON format and returns each message as a <see cref="SessionMessage"/> object.
    /// </summary>
    /// <param name="filePath">The path to the file containing the session messages.</param>
    /// <returns>The <see cref="SessionMessage"/> objects parsed from the file.</returns>
    /// <exception cref="IOException">Thrown if the file cannot be opened for shared reading after retrying.</exception>
    public IEnumerable<SessionMessage> ParseFile(string filePath)
    {
        return ParseFrom(filePath, 0).Rows;
    }

    /// <summary>
    /// Parses the lines of a transcript file that start at or after <paramref name="startOffset"/>. Transcripts are
    /// append-only, so passing the <see cref="ParsedRange.EndOffset"/> of an earlier parse yields exactly the lines
    /// appended since then. A trailing line that is not terminated by a newline and is not yet valid JSON (i.e. the
    /// writer is mid-line) is left unconsumed so that the next call picks it up once it is complete.
    /// </summary>
    /// <param name="filePath">The path to the file containing the session messages.</param>
    /// <param name="startOffset">The byte offset of the first line to parse; must be the start of a line.</param>
    /// <returns>The parsed rows together with the byte offset just past the last line that was consumed.</returns>
    /// <exception cref="IOException">Thrown if the file cannot be opened for shared reading after retrying.</exception>
    public ParsedRange ParseFrom(string filePath, long startOffset)
    {
        using var stream = OpenWithRetry(filePath);
        stream.Seek(startOffset, SeekOrigin.Begin);

        var rows = new List<SessionMessage>();
        var buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        try
        {
            // buffer[start..filled] holds the bytes read but not yet consumed; bufferOffset is buffer[0]'s file offset.
            var filled = 0;
            var start = 0;
            var bufferOffset = startOffset;
            var endOffset = startOffset;

            while (true)
            {
                int newline;
                while ((newline = buffer.AsSpan(start, filled - start).IndexOf((byte)'\n')) >= 0)
                {
                    TryParseLine(buffer.AsSpan(start, newline), bufferOffset + start, filePath, rows);
                    start += newline + 1;
                    endOffset = bufferOffset + start;
                }

                if (start > 0)
                {
                    Buffer.BlockCopy(buffer, start, buffer, 0, filled - start);
                    bufferOffset += start;
                    filled -= start;
                    start = 0;
                }

                if (filled == buffer.Length)
                {
                    var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, larger, 0, filled);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                var read = stream.Read(buffer, filled, buffer.Length - filled);
                if (read == 0)
                {
                    if (filled > 0 && TryParseLine(buffer.AsSpan(0, filled), bufferOffset, filePath, rows))
                    {
                        endOffset = bufferOffset + filled;
                    }

                    return new(rows, endOffset);
                }

                filled += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Parses one transcript line and appends the resulting row, if any.
    /// </summary>
    /// <param name="line">The line's UTF-8 bytes, without the trailing newline.</param>
    /// <param name="lineOffset">The line's byte offset in the file, used to recognise a UTF-8 byte-order mark.</param>
    /// <param name="filePath">The file being parsed, for diagnostics.</param>
    /// <param name="rows">The list the parsed row is appended to.</param>
    /// <returns><see langword="false"/> if the line was non-blank but not valid JSON; otherwise <see langword="true"/>.</returns>
    private bool TryParseLine(ReadOnlySpan<byte> line, long lineOffset, string filePath, List<SessionMessage> rows)
    {
        if (lineOffset == 0 && line.StartsWith(Utf8Bom))
        {
            line = line[3..];
        }

        if (line.IsEmpty || IsAllWhiteSpace(line))
        {
            return true;
        }

        try
        {
            var msg = JsonSerializer.Deserialize(line, CoreJsonContext.Default.SessionMessage);
            if (msg is not null)
            {
                rows.Add(msg);
            }

            return true;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse line in {File}", filePath);

            return false;
        }
    }

    /// <summary>
    /// Determines whether a line consists only of JSON whitespace (spaces, tabs, carriage returns).
    /// </summary>
    /// <param name="line">The line's UTF-8 bytes.</param>
    /// <returns><see langword="true"/> if the line has no content.</returns>
    private static bool IsAllWhiteSpace(ReadOnlySpan<byte> line)
    {
        foreach (var b in line)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
            {
                return false;
            }
        }

        return true;
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
            .Select(m => m.Timestamp!.Value)
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
            .DistinctBy(m => (m.Message!.Id, m.RequestId) is (null, null)
                ? (object)m.Uuid!
                : (m.Message!.Id, m.RequestId))
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

    /// <summary>
    /// Opens the given file for shared reading, retrying briefly if another process (e.g. the Claude CLI)
    /// currently has an exclusive lock on it.
    /// </summary>
    /// <param name="filePath">The path to the file to open.</param>
    /// <returns>An open <see cref="FileStream"/> for the file.</returns>
    private static FileStream OpenWithRetry(string filePath)
    {
        const int maxAttempts = 3;
        var delay = TimeSpan.FromMilliseconds(50);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex) when (attempt < maxAttempts && (ex is IOException or UnauthorizedAccessException))
            {
                Thread.Sleep(delay);
                delay += delay;
            }
        }
    }
}

/// <summary>
/// The result of parsing a byte range of a transcript file.
/// </summary>
/// <param name="Rows">The rows parsed from the range, in file order.</param>
/// <param name="EndOffset">The byte offset just past the last line that was consumed; the start of the next unread line.</param>
public sealed record ParsedRange(List<SessionMessage> Rows, long EndOffset);
