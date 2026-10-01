using AIUsageMonitor.Core.Providers.Claude.Models;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Persists the parsed rows of each session transcript under the user's local application data folder, so that a
/// new process (e.g. every one-shot CLI invocation) does not have to re-parse transcripts that have not changed.
/// One compact binary file is kept per transcript; any problem reading or writing it is treated as a cache miss,
/// because the transcripts themselves remain the source of truth.
/// </summary>
/// <param name="directory">The directory the cache files are stored in; created on first write.</param>
/// <param name="logger">The logger used to record cache problems.</param>
public sealed class SessionRowDiskCache(string directory, ILogger<SessionRowDiskCache> logger)
{
    /// <summary>
    /// The identifying bytes at the start of every cache file ("AIMR").
    /// </summary>
    private const int Magic = 0x524D4941;

    /// <summary>
    /// The layout version of cache files; bump it whenever the layout or the meaning of a stored row changes,
    /// and every existing file is then ignored and rebuilt.
    /// </summary>
    private const int FormatVersion = 1;

    /// <summary>
    /// The file extension of cache entries.
    /// </summary>
    private const string EntryExtension = ".rows";

    /// <summary>
    /// Gets the default cache directory (<c>%LOCALAPPDATA%\aimon\cache\session-rows</c>).
    /// </summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aimon", "cache", "session-rows");

    /// <summary>
    /// Loads the cached parse of a transcript.
    /// </summary>
    /// <param name="filePath">The transcript file the entry was built from.</param>
    /// <returns>The cached state, or <see langword="null"/> when there is no usable entry (missing, other format, corrupt).</returns>
    public CachedSessionFile? TryLoad(string filePath)
    {
        try
        {
            var entryPath = GetEntryPath(filePath);

            return File.Exists(entryPath) ? Deserialize(File.ReadAllBytes(entryPath), filePath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or FormatException or ArgumentException or OverflowException)
        {
            logger.LogDebug(ex, "Ignoring unreadable cache entry for {File}", filePath);

            return null;
        }
    }

    /// <summary>
    /// Stores the parsed state of a transcript, replacing any earlier entry. Failures are logged and ignored.
    /// </summary>
    /// <param name="filePath">The transcript file the entry was built from.</param>
    /// <param name="entry">The state to store.</param>
    public void Save(string filePath, CachedSessionFile entry)
    {
        try
        {
            Directory.CreateDirectory(directory);

            // Written in place rather than via a temporary file plus rename: on Windows every extra file operation
            // is scanned by antivirus and roughly doubles the cost of the first (cold) run. This is safe because a
            // reader that meets a half-written entry (a concurrent process, or a crash) fails to decode it and
            // simply treats it as a miss.
            File.WriteAllBytes(GetEntryPath(filePath), Serialize(filePath, entry));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not write cache entry for {File}", filePath);
        }
    }

    /// <summary>
    /// Deletes the entry for a single transcript, if any.
    /// </summary>
    /// <param name="filePath">The transcript file whose entry should be dropped.</param>
    public void Remove(string filePath)
    {
        DeleteQuietly(GetEntryPath(filePath));
    }

    /// <summary>
    /// Deletes the entries of transcripts that no longer exist.
    /// Does nothing when <paramref name="currentFiles"/> is empty, so a transient failure to enumerate the
    /// transcripts cannot wipe the whole cache.
    /// </summary>
    /// <param name="currentFiles">The transcript files that currently exist.</param>
    public void Prune(IReadOnlyCollection<string> currentFiles)
    {
        if (currentFiles.Count == 0 || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            var keep = currentFiles.Select(GetEntryPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(directory, $"*{EntryExtension}"))
            {
                if (!keep.Contains(file))
                {
                    DeleteQuietly(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not prune the session row cache");
        }
    }

    /// <summary>
    /// Gets the path of the cache file for a transcript, named after a hash of the transcript's path.
    /// </summary>
    /// <param name="filePath">The transcript file.</param>
    /// <returns>The full path of the cache entry.</returns>
    private string GetEntryPath(string filePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(filePath.ToUpperInvariant()));

        return Path.Combine(directory, Convert.ToHexString(hash.AsSpan(0, 16)) + EntryExtension);
    }

    /// <summary>
    /// Deletes a file, ignoring a missing file or any I/O failure.
    /// </summary>
    /// <param name="path">The file to delete, or <see langword="null"/> to do nothing.</param>
    private static void DeleteQuietly(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a file that cannot be removed now is retried by the next prune.
        }
    }

    /// <summary>
    /// Encodes an entry. Every distinct string (type, ids, model, session, cwd, ...) is stored once in a table and
    /// referenced by index, since a transcript repeats the same few values on thousands of rows.
    /// </summary>
    /// <param name="filePath">The transcript file, stored so a hash collision can never serve another file's rows.</param>
    /// <param name="entry">The entry to encode.</param>
    /// <returns>The encoded bytes.</returns>
    internal static byte[] Serialize(string filePath, CachedSessionFile entry)
    {
        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        var strings = new List<string>();

        int Index(string? value)
        {
            if (value is null)
            {
                return 0;
            }

            if (!table.TryGetValue(value, out var index))
            {
                strings.Add(value);
                index = strings.Count;
                table[value] = index;
            }

            return index;
        }

        using var rowStream = new MemoryStream();
        using (var rowWriter = new BinaryWriter(rowStream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var row in entry.Rows)
            {
                WriteRow(rowWriter, row, Index);
            }
        }

        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(filePath);
            writer.Write(entry.Length);
            writer.Write(entry.LastWriteUtcTicks);
            writer.Write(entry.ParsedBytes);
            writer.Write7BitEncodedInt(entry.Fingerprint.Length);
            writer.Write(entry.Fingerprint);
            writer.Write7BitEncodedInt(strings.Count);
            foreach (var value in strings)
            {
                writer.Write(value);
            }

            writer.Write7BitEncodedInt(entry.Rows.Count);
        }

        rowStream.WriteTo(output);

        return output.ToArray();
    }

    /// <summary>
    /// Decodes an entry produced by <see cref="Serialize"/>.
    /// </summary>
    /// <param name="data">The encoded bytes.</param>
    /// <param name="filePath">The transcript the entry is expected to describe.</param>
    /// <returns>The entry, or <see langword="null"/> if it was written by another format version or for another file.</returns>
    /// <exception cref="InvalidDataException">Thrown if the data is not a valid entry.</exception>
    internal static CachedSessionFile? Deserialize(byte[] data, string filePath)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        if (reader.ReadInt32() != Magic || reader.ReadInt32() != FormatVersion)
        {
            return null;
        }

        if (!string.Equals(reader.ReadString(), filePath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var length = reader.ReadInt64();
        var lastWriteTicks = reader.ReadInt64();
        var parsedBytes = reader.ReadInt64();
        var fingerprint = reader.ReadBytes(ReadCount(reader, data.Length));

        var strings = new string?[ReadCount(reader, data.Length) + 1];
        for (var i = 1; i < strings.Length; i++)
        {
            strings[i] = reader.ReadString();
        }

        string? Lookup(int index) => index >= 0 && index < strings.Length
            ? strings[index]
            : throw new InvalidDataException("String index out of range.");

        var rowCount = ReadCount(reader, data.Length);
        var rows = new List<SessionMessage>(rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            rows.Add(ReadRow(reader, Lookup));
        }

        return new(length, lastWriteTicks, parsedBytes, fingerprint, rows);
    }

    /// <summary>
    /// Reads a count and rejects values that cannot possibly fit in the data, so corruption cannot trigger a huge allocation.
    /// </summary>
    /// <param name="reader">The reader positioned on the count.</param>
    /// <param name="dataLength">The total size of the data being decoded.</param>
    /// <returns>The count.</returns>
    /// <exception cref="InvalidDataException">Thrown if the count is negative or larger than the data.</exception>
    private static int ReadCount(BinaryReader reader, int dataLength)
    {
        var count = reader.Read7BitEncodedInt();

        return count >= 0 && count <= dataLength ? count : throw new InvalidDataException("Invalid count.");
    }

    /// <summary>
    /// Presence bits of the optional fields of a row.
    /// </summary>
    [Flags]
    private enum RowFlags : byte
    {
        /// <summary>
        /// The row has a working directory.
        /// </summary>
        Cwd = 1,

        /// <summary>
        /// The row has a message body.
        /// </summary>
        Message = 2,

        /// <summary>
        /// The row has a request id.
        /// </summary>
        RequestId = 4,

        /// <summary>
        /// The row has a session id.
        /// </summary>
        SessionId = 8,

        /// <summary>
        /// The row has a timestamp.
        /// </summary>
        Timestamp = 16,

        /// <summary>
        /// The row has a UUID.
        /// </summary>
        Uuid = 32,
    }

    /// <summary>
    /// Presence bits of the optional fields of a message body.
    /// </summary>
    [Flags]
    private enum MessageFlags : byte
    {
        /// <summary>
        /// The message has an API id.
        /// </summary>
        Id = 1,

        /// <summary>
        /// The message has a model name.
        /// </summary>
        Model = 2,

        /// <summary>
        /// The message has token usage.
        /// </summary>
        Usage = 4,

        /// <summary>
        /// The usage carries the cache-creation TTL breakdown.
        /// </summary>
        CacheCreation = 8,
    }

    /// <summary>
    /// Encodes one row.
    /// </summary>
    /// <param name="writer">The writer to encode to.</param>
    /// <param name="row">The row to encode.</param>
    /// <param name="index">Maps a string to its table index (0 for <see langword="null"/>).</param>
    private static void WriteRow(BinaryWriter writer, SessionMessage row, Func<string?, int> index)
    {
        var flags = RowFlags.Cwd.Only(row.Cwd is not null)
                    | RowFlags.Message.Only(row.Message is not null)
                    | RowFlags.RequestId.Only(row.RequestId is not null)
                    | RowFlags.SessionId.Only(row.SessionId is not null)
                    | RowFlags.Timestamp.Only(row.Timestamp is not null)
                    | RowFlags.Uuid.Only(row.Uuid is not null);

        writer.Write((byte)flags);
        writer.Write7BitEncodedInt(index(row.Type));

        if (row.Cwd is not null)
        {
            writer.Write7BitEncodedInt(index(row.Cwd));
        }

        if (row.RequestId is not null)
        {
            writer.Write7BitEncodedInt(index(row.RequestId));
        }

        if (row.SessionId is not null)
        {
            writer.Write7BitEncodedInt(index(row.SessionId));
        }

        if (row.Uuid is not null)
        {
            writer.Write7BitEncodedInt(index(row.Uuid));
        }

        if (row.Timestamp is { } timestamp)
        {
            writer.Write7BitEncodedInt64(timestamp.UtcTicks);
            writer.Write7BitEncodedInt64(timestamp.Offset.Ticks);
        }

        if (row.Message is { } message)
        {
            WriteMessage(writer, message, index);
        }
    }

    /// <summary>
    /// Encodes a message body.
    /// </summary>
    /// <param name="writer">The writer to encode to.</param>
    /// <param name="message">The message body to encode.</param>
    /// <param name="index">Maps a string to its table index (0 for <see langword="null"/>).</param>
    private static void WriteMessage(BinaryWriter writer, MessageContent message, Func<string?, int> index)
    {
        var flags = MessageFlags.Id.Only(message.Id is not null)
                    | MessageFlags.Model.Only(message.Model is not null)
                    | MessageFlags.Usage.Only(message.Usage is not null)
                    | MessageFlags.CacheCreation.Only(message.Usage?.CacheCreation is not null);

        writer.Write((byte)flags);
        writer.Write7BitEncodedInt(index(message.Role));
        writer.Write7BitEncodedInt(message.ToolUseCount);

        if (message.Id is not null)
        {
            writer.Write7BitEncodedInt(index(message.Id));
        }

        if (message.Model is not null)
        {
            writer.Write7BitEncodedInt(index(message.Model));
        }

        if (message.Usage is { } usage)
        {
            writer.Write7BitEncodedInt64(usage.InputTokens);
            writer.Write7BitEncodedInt64(usage.OutputTokens);
            writer.Write7BitEncodedInt64(usage.CacheReadInputTokens);
            writer.Write7BitEncodedInt64(usage.CacheCreationInputTokens);

            if (usage.CacheCreation is { } detail)
            {
                writer.Write7BitEncodedInt64(detail.Ephemeral5mInputTokens);
                writer.Write7BitEncodedInt64(detail.Ephemeral1hInputTokens);
            }
        }
    }

    /// <summary>
    /// Decodes one row.
    /// </summary>
    /// <param name="reader">The reader positioned on the row.</param>
    /// <param name="lookup">Resolves a string table index to its string (<see langword="null"/> for index 0).</param>
    /// <returns>The decoded row.</returns>
    private static SessionMessage ReadRow(BinaryReader reader, Func<int, string?> lookup)
    {
        var flags = (RowFlags)reader.ReadByte();
        var type = lookup(reader.Read7BitEncodedInt()) ?? "";
        var cwd = flags.HasFlag(RowFlags.Cwd) ? lookup(reader.Read7BitEncodedInt()) : null;
        var requestId = flags.HasFlag(RowFlags.RequestId) ? lookup(reader.Read7BitEncodedInt()) : null;
        var sessionId = flags.HasFlag(RowFlags.SessionId) ? lookup(reader.Read7BitEncodedInt()) : null;
        var uuid = flags.HasFlag(RowFlags.Uuid) ? lookup(reader.Read7BitEncodedInt()) : null;

        DateTimeOffset? timestamp = null;
        if (flags.HasFlag(RowFlags.Timestamp))
        {
            var utcTicks = reader.Read7BitEncodedInt64();
            var offsetTicks = reader.Read7BitEncodedInt64();
            timestamp = new DateTimeOffset(utcTicks, TimeSpan.Zero).ToOffset(new TimeSpan(offsetTicks));
        }

        return new()
        {
            Type = type,
            Cwd = cwd,
            RequestId = requestId,
            SessionId = sessionId,
            Uuid = uuid,
            Timestamp = timestamp,
            Message = flags.HasFlag(RowFlags.Message) ? ReadMessage(reader, lookup) : null,
        };
    }

    /// <summary>
    /// Decodes a message body.
    /// </summary>
    /// <param name="reader">The reader positioned on the message body.</param>
    /// <param name="lookup">Resolves a string table index to its string (<see langword="null"/> for index 0).</param>
    /// <returns>The decoded message body.</returns>
    private static MessageContent ReadMessage(BinaryReader reader, Func<int, string?> lookup)
    {
        var flags = (MessageFlags)reader.ReadByte();
        var role = lookup(reader.Read7BitEncodedInt()) ?? "";
        var toolUseCount = reader.Read7BitEncodedInt();
        var id = flags.HasFlag(MessageFlags.Id) ? lookup(reader.Read7BitEncodedInt()) : null;
        var model = flags.HasFlag(MessageFlags.Model) ? lookup(reader.Read7BitEncodedInt()) : null;

        TokenUsage? usage = null;
        if (flags.HasFlag(MessageFlags.Usage))
        {
            var input = reader.Read7BitEncodedInt64();
            var output = reader.Read7BitEncodedInt64();
            var cacheRead = reader.Read7BitEncodedInt64();
            var cacheCreation = reader.Read7BitEncodedInt64();
            CacheCreationDetail? detail = flags.HasFlag(MessageFlags.CacheCreation)
                ? new()
                {
                    Ephemeral5mInputTokens = reader.Read7BitEncodedInt64(),
                    Ephemeral1hInputTokens = reader.Read7BitEncodedInt64(),
                }
                : null;

            usage = new()
            {
                InputTokens = input,
                OutputTokens = output,
                CacheReadInputTokens = cacheRead,
                CacheCreationInputTokens = cacheCreation,
                CacheCreation = detail,
            };
        }

        return new()
        {
            Role = role,
            ToolUseCount = toolUseCount,
            Id = id,
            Model = model,
            Usage = usage,
        };
    }
}

/// <summary>
/// Helpers for composing flag enums.
/// </summary>
file static class FlagExtensions
{
    /// <summary>
    /// Returns the flag when <paramref name="condition"/> holds, otherwise no flags.
    /// </summary>
    /// <typeparam name="T">The flag enum type, backed by <see cref="byte"/>.</typeparam>
    /// <param name="flag">The flag.</param>
    /// <param name="condition">Whether the flag applies.</param>
    /// <returns><paramref name="flag"/> or the zero value.</returns>
    public static T Only<T>(this T flag, bool condition) where T : struct, Enum
    {
        return condition ? flag : default;
    }
}
