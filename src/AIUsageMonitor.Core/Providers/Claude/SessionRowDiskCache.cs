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
    private const int FormatVersion = 3;

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
                                       or FormatException or ArgumentException or OverflowException
                                       or EndOfStreamException)
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
            rows.Add(ReadRow(reader, Lookup, data.Length));
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
    private enum RowFlags
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

        /// <summary>
        /// The row is written by a sub-agent.
        /// </summary>
        IsSidechain = 64,

        /// <summary>
        /// The row is a synthetic API-error message.
        /// </summary>
        IsApiErrorMessage = 128,

        /// <summary>
        /// The row is the summary of a compacted conversation.
        /// </summary>
        IsCompactSummary = 256,

        /// <summary>
        /// The row has string fields beyond the basic ones; a bit mask of them follows.
        /// </summary>
        Strings = 512,

        /// <summary>
        /// The row has whole-number fields; a bit mask of them follows.
        /// </summary>
        Numbers = 1024,

        /// <summary>
        /// The row is a cost snapshot with decimal fields; a bit mask of them follows.
        /// </summary>
        Decimals = 2048,

        /// <summary>
        /// The row has compaction metadata.
        /// </summary>
        Compact = 4096,

        /// <summary>
        /// The row has a permission decision.
        /// </summary>
        Permission = 8192,

        /// <summary>
        /// The row has rate-limit state.
        /// </summary>
        Quota = 16384,

        /// <summary>
        /// The row has per-model usage of a cost snapshot.
        /// </summary>
        ModelUsage = 32768,
    }

    /// <summary>
    /// Presence bits of the optional fields of a message body.
    /// </summary>
    [Flags]
    private enum MessageFlags
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

        /// <summary>
        /// The message has a stop reason.
        /// </summary>
        StopReason = 16,

        /// <summary>
        /// The message content has blocks other than tool calls (or the names of its tool calls).
        /// </summary>
        Content = 32,

        /// <summary>
        /// The message has a cache-miss diagnostic.
        /// </summary>
        CacheMiss = 64,

        /// <summary>
        /// The usage carries service tier, speed, thinking tokens or server tool usage; a bit mask of them follows.
        /// </summary>
        UsageExtras = 128,
    }

    /// <summary>
    /// Presence bits of the optional extras of a usage record.
    /// </summary>
    [Flags]
    private enum UsageExtraFlags
    {
        /// <summary>
        /// The usage has a service tier.
        /// </summary>
        ServiceTier = 1,

        /// <summary>
        /// The usage has a speed mode.
        /// </summary>
        Speed = 2,

        /// <summary>
        /// The usage has output-token details.
        /// </summary>
        OutputDetails = 4,

        /// <summary>
        /// The usage has server tool usage.
        /// </summary>
        ServerToolUse = 8,
    }

    /// <summary>
    /// Accessors for the optional string fields of a row, in the order of their presence bits.
    /// </summary>
    private static readonly Func<SessionMessage, string?>[] StringGetters =
    [
        r => r.AgentId,
        r => r.AgentName,
        r => r.AiTitle,
        r => r.AdvisorModel,
        r => r.AttributionAgent,
        r => r.AttributionMcpServer,
        r => r.AttributionMcpTool,
        r => r.AttributionPlugin,
        r => r.AttributionSkill,
        r => r.CustomTitle,
        r => r.Effort,
        r => r.Entrypoint,
        r => r.Error,
        r => r.GitBranch,
        r => r.PermissionMode,
        r => r.PerTurnEffort,
        r => r.PrRepository,
        r => r.PrUrl,
        r => r.RequestedModel,
        r => r.Subtype,
        r => r.ToolDenialKind,
        r => r.Version,
        r => r.Origin?.Kind,
    ];

    /// <summary>
    /// Accessors for the optional whole-number fields of a row, in the order of their presence bits.
    /// </summary>
    private static readonly Func<SessionMessage, long?>[] NumberGetters =
    [
        r => r.ApiErrorStatus,
        r => r.PrNumber,
        r => r.ThinkingDurationMs,
        r => r.TotalLinesAdded,
        r => r.TotalLinesRemoved,
        r => r.StartTimeMs,
    ];

    /// <summary>
    /// Accessors for the optional decimal fields of a row, in the order of their presence bits.
    /// </summary>
    private static readonly Func<SessionMessage, double?>[] DecimalGetters =
    [
        r => r.TotalApiDurationMs,
        r => r.TotalCostUsd,
        r => r.TotalDurationMs,
        r => r.TotalToolDurationMs,
    ];

    /// <summary>
    /// Encodes one row.
    /// </summary>
    /// <param name="writer">The writer to encode to.</param>
    /// <param name="row">The row to encode.</param>
    /// <param name="index">Maps a string to its table index (0 for <see langword="null"/>).</param>
    private static void WriteRow(BinaryWriter writer, SessionMessage row, Func<string?, int> index)
    {
        var stringMask = Mask(StringGetters, row);
        var numberMask = Mask(NumberGetters, row);
        var decimalMask = Mask(DecimalGetters, row);

        var flags = RowFlags.Cwd.Only(row.Cwd is not null)
                    | RowFlags.Message.Only(row.Message is not null)
                    | RowFlags.RequestId.Only(row.RequestId is not null)
                    | RowFlags.SessionId.Only(row.SessionId is not null)
                    | RowFlags.Timestamp.Only(row.Timestamp is not null)
                    | RowFlags.Uuid.Only(row.Uuid is not null)
                    | RowFlags.IsSidechain.Only(row.IsSidechain)
                    | RowFlags.IsApiErrorMessage.Only(row.IsApiErrorMessage)
                    | RowFlags.IsCompactSummary.Only(row.IsCompactSummary)
                    | RowFlags.Strings.Only(stringMask != 0)
                    | RowFlags.Numbers.Only(numberMask != 0)
                    | RowFlags.Decimals.Only(decimalMask != 0)
                    | RowFlags.Compact.Only(row.CompactMetadata is not null)
                    | RowFlags.Permission.Only(row.PermissionDecision is not null)
                    | RowFlags.Quota.Only(row.QuotaLimits is not null)
                    | RowFlags.ModelUsage.Only(row.ModelUsage is { Count: > 0 });

        writer.Write7BitEncodedInt((int)flags);
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

        if (stringMask != 0)
        {
            writer.Write7BitEncodedInt64(stringMask);
            for (var bit = 0; bit < StringGetters.Length; bit++)
            {
                if (StringGetters[bit](row) is { } value)
                {
                    writer.Write7BitEncodedInt(index(value));
                }
            }
        }

        if (numberMask != 0)
        {
            writer.Write7BitEncodedInt64(numberMask);
            for (var bit = 0; bit < NumberGetters.Length; bit++)
            {
                if (NumberGetters[bit](row) is { } value)
                {
                    writer.Write7BitEncodedInt64(value);
                }
            }
        }

        if (decimalMask != 0)
        {
            writer.Write7BitEncodedInt64(decimalMask);
            for (var bit = 0; bit < DecimalGetters.Length; bit++)
            {
                if (DecimalGetters[bit](row) is { } value)
                {
                    writer.Write(value);
                }
            }
        }

        if (row.CompactMetadata is { } compact)
        {
            writer.Write7BitEncodedInt(index(compact.Trigger));
            writer.Write7BitEncodedInt64(compact.PreTokens);
            writer.Write7BitEncodedInt64(compact.PostTokens);
            writer.Write7BitEncodedInt64(compact.DurationMs);
        }

        if (row.PermissionDecision is { } permission)
        {
            writer.Write7BitEncodedInt(index(permission.Decision));
            writer.Write7BitEncodedInt(index(permission.Source));
        }

        if (row.QuotaLimits is { } quota)
        {
            writer.Write7BitEncodedInt(index(quota.Status));
            writer.Write7BitEncodedInt64(quota.ResetsAt ?? -1);
            writer.Write7BitEncodedInt(index(quota.RateLimitType));
            writer.Write7BitEncodedInt(index(quota.OverageStatus));
            writer.Write(quota.IsUsingOverage);
        }

        if (row.ModelUsage is { Count: > 0 } modelUsage)
        {
            writer.Write7BitEncodedInt(modelUsage.Count);
            foreach (var (model, usage) in modelUsage)
            {
                writer.Write7BitEncodedInt(index(model));
                writer.Write7BitEncodedInt64(usage.InputTokens);
                writer.Write7BitEncodedInt64(usage.OutputTokens);
                writer.Write7BitEncodedInt64(usage.ThinkingTokens);
                writer.Write7BitEncodedInt64(usage.CacheReadInputTokens);
                writer.Write7BitEncodedInt64(usage.CacheCreationInputTokens);
                writer.Write7BitEncodedInt64(usage.WebSearchRequests);
                writer.Write(usage.CostUsd);
            }
        }

        if (row.Message is { } message)
        {
            WriteMessage(writer, message, index);
        }
    }

    /// <summary>
    /// Builds the presence mask of a list of optional-field accessors for a row.
    /// </summary>
    /// <typeparam name="T">The type of the field.</typeparam>
    /// <param name="getters">The accessors; bit <c>i</c> of the result is set when accessor <c>i</c> yields a value.</param>
    /// <param name="row">The row.</param>
    /// <returns>The presence mask.</returns>
    private static long Mask<T>(Func<SessionMessage, T>[] getters, SessionMessage row)
    {
        var mask = 0L;
        for (var bit = 0; bit < getters.Length; bit++)
        {
            if (getters[bit](row) is not null)
            {
                mask |= 1L << bit;
            }
        }

        return mask;
    }

    /// <summary>
    /// Encodes a message body.
    /// </summary>
    /// <param name="writer">The writer to encode to.</param>
    /// <param name="message">The message body to encode.</param>
    /// <param name="index">Maps a string to its table index (0 for <see langword="null"/>).</param>
    private static void WriteMessage(BinaryWriter writer, MessageContent message, Func<string?, int> index)
    {
        var content = message.Content;
        var hasContentDetail = content.ToolResultCount + content.ToolResultErrorCount
                               + content.ThinkingBlockCount + content.TextBlockCount > 0;
        var usage = message.Usage;
        var extras = UsageExtraFlags.ServiceTier.Only(usage?.ServiceTier is not null)
                     | UsageExtraFlags.Speed.Only(usage?.Speed is not null)
                     | UsageExtraFlags.OutputDetails.Only(usage?.OutputTokensDetails is not null)
                     | UsageExtraFlags.ServerToolUse.Only(usage?.ServerToolUse is not null);

        var flags = MessageFlags.Id.Only(message.Id is not null)
                    | MessageFlags.Model.Only(message.Model is not null)
                    | MessageFlags.Usage.Only(usage is not null)
                    | MessageFlags.CacheCreation.Only(usage?.CacheCreation is not null)
                    | MessageFlags.StopReason.Only(message.StopReason is not null)
                    | MessageFlags.Content.Only(hasContentDetail)
                    | MessageFlags.CacheMiss.Only(message.Diagnostics?.CacheMissReason is not null)
                    | MessageFlags.UsageExtras.Only(extras != 0);

        writer.Write7BitEncodedInt((int)flags);
        writer.Write7BitEncodedInt(index(message.Role));
        writer.Write7BitEncodedInt(content.ToolNames.Count);
        foreach (var name in content.ToolNames)
        {
            writer.Write7BitEncodedInt(index(name));
        }

        if (hasContentDetail)
        {
            writer.Write7BitEncodedInt(content.ToolResultCount);
            writer.Write7BitEncodedInt(content.ToolResultErrorCount);
            writer.Write7BitEncodedInt(content.ThinkingBlockCount);
            writer.Write7BitEncodedInt(content.TextBlockCount);
        }

        if (message.Id is not null)
        {
            writer.Write7BitEncodedInt(index(message.Id));
        }

        if (message.Model is not null)
        {
            writer.Write7BitEncodedInt(index(message.Model));
        }

        if (message.StopReason is not null)
        {
            writer.Write7BitEncodedInt(index(message.StopReason));
        }

        if (usage is not null)
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

            if (extras != 0)
            {
                writer.Write7BitEncodedInt((int)extras);
                if (usage.ServiceTier is not null)
                {
                    writer.Write7BitEncodedInt(index(usage.ServiceTier));
                }

                if (usage.Speed is not null)
                {
                    writer.Write7BitEncodedInt(index(usage.Speed));
                }

                if (usage.OutputTokensDetails is { } details)
                {
                    writer.Write7BitEncodedInt64(details.ThinkingTokens);
                }

                if (usage.ServerToolUse is { } server)
                {
                    writer.Write7BitEncodedInt64(server.WebSearchRequests);
                    writer.Write7BitEncodedInt64(server.WebFetchRequests);
                }
            }
        }

        if (message.Diagnostics?.CacheMissReason is { } miss)
        {
            writer.Write7BitEncodedInt(index(miss.Type));
            writer.Write7BitEncodedInt64(miss.CacheMissedInputTokens);
        }
    }

    /// <summary>
    /// Decodes one row.
    /// </summary>
    /// <param name="reader">The reader positioned on the row.</param>
    /// <param name="lookup">Resolves a string table index to its string (<see langword="null"/> for index 0).</param>
    /// <param name="dataLength">The total size of the data being decoded, used to reject impossible counts.</param>
    /// <returns>The decoded row.</returns>
    private static SessionMessage ReadRow(BinaryReader reader, Func<int, string?> lookup, int dataLength)
    {
        var flags = (RowFlags)reader.Read7BitEncodedInt();
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

        var strings = new string?[StringGetters.Length];
        if (flags.HasFlag(RowFlags.Strings))
        {
            var mask = reader.Read7BitEncodedInt64();
            for (var bit = 0; bit < strings.Length; bit++)
            {
                if ((mask & (1L << bit)) != 0)
                {
                    strings[bit] = lookup(reader.Read7BitEncodedInt());
                }
            }
        }

        var numbers = new long?[NumberGetters.Length];
        if (flags.HasFlag(RowFlags.Numbers))
        {
            var mask = reader.Read7BitEncodedInt64();
            for (var bit = 0; bit < numbers.Length; bit++)
            {
                if ((mask & (1L << bit)) != 0)
                {
                    numbers[bit] = reader.Read7BitEncodedInt64();
                }
            }
        }

        var decimals = new double?[DecimalGetters.Length];
        if (flags.HasFlag(RowFlags.Decimals))
        {
            var mask = reader.Read7BitEncodedInt64();
            for (var bit = 0; bit < decimals.Length; bit++)
            {
                if ((mask & (1L << bit)) != 0)
                {
                    decimals[bit] = reader.ReadDouble();
                }
            }
        }

        CompactMetadata? compact = null;
        if (flags.HasFlag(RowFlags.Compact))
        {
            compact = new()
            {
                Trigger = lookup(reader.Read7BitEncodedInt()),
                PreTokens = reader.Read7BitEncodedInt64(),
                PostTokens = reader.Read7BitEncodedInt64(),
                DurationMs = reader.Read7BitEncodedInt64(),
            };
        }

        PermissionDecision? permission = null;
        if (flags.HasFlag(RowFlags.Permission))
        {
            permission = new()
            {
                Decision = lookup(reader.Read7BitEncodedInt()),
                Source = lookup(reader.Read7BitEncodedInt()),
            };
        }

        QuotaLimits? quota = null;
        if (flags.HasFlag(RowFlags.Quota))
        {
            var status = lookup(reader.Read7BitEncodedInt());
            var resetsAt = reader.Read7BitEncodedInt64();
            quota = new()
            {
                Status = status,
                ResetsAt = resetsAt >= 0 ? resetsAt : null,
                RateLimitType = lookup(reader.Read7BitEncodedInt()),
                OverageStatus = lookup(reader.Read7BitEncodedInt()),
                IsUsingOverage = reader.ReadBoolean(),
            };
        }

        Dictionary<string, CostStateModelUsage>? modelUsage = null;
        if (flags.HasFlag(RowFlags.ModelUsage))
        {
            var count = ReadCount(reader, dataLength);
            modelUsage = new(count, StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var model = lookup(reader.Read7BitEncodedInt()) ?? "";
                modelUsage[model] = new()
                {
                    InputTokens = reader.Read7BitEncodedInt64(),
                    OutputTokens = reader.Read7BitEncodedInt64(),
                    ThinkingTokens = reader.Read7BitEncodedInt64(),
                    CacheReadInputTokens = reader.Read7BitEncodedInt64(),
                    CacheCreationInputTokens = reader.Read7BitEncodedInt64(),
                    WebSearchRequests = reader.Read7BitEncodedInt64(),
                    CostUsd = reader.ReadDouble(),
                };
            }
        }

        var message = flags.HasFlag(RowFlags.Message) ? ReadMessage(reader, lookup, dataLength) : null;

        return new()
        {
            Type = type,
            Cwd = cwd,
            RequestId = requestId,
            SessionId = sessionId,
            Uuid = uuid,
            Timestamp = timestamp,
            IsSidechain = flags.HasFlag(RowFlags.IsSidechain),
            IsApiErrorMessage = flags.HasFlag(RowFlags.IsApiErrorMessage),
            IsCompactSummary = flags.HasFlag(RowFlags.IsCompactSummary),
            AgentId = strings[0],
            AgentName = strings[1],
            AiTitle = strings[2],
            AdvisorModel = strings[3],
            AttributionAgent = strings[4],
            AttributionMcpServer = strings[5],
            AttributionMcpTool = strings[6],
            AttributionPlugin = strings[7],
            AttributionSkill = strings[8],
            CustomTitle = strings[9],
            Effort = strings[10],
            Entrypoint = strings[11],
            Error = strings[12],
            GitBranch = strings[13],
            PermissionMode = strings[14],
            PerTurnEffort = strings[15],
            PrRepository = strings[16],
            PrUrl = strings[17],
            RequestedModel = strings[18],
            Subtype = strings[19],
            ToolDenialKind = strings[20],
            Version = strings[21],
            Origin = strings[22] is null ? null : new() { Kind = strings[22] },
            ApiErrorStatus = numbers[0],
            PrNumber = numbers[1],
            ThinkingDurationMs = numbers[2],
            TotalLinesAdded = numbers[3],
            TotalLinesRemoved = numbers[4],
            StartTimeMs = numbers[5],
            TotalApiDurationMs = decimals[0],
            TotalCostUsd = decimals[1],
            TotalDurationMs = decimals[2],
            TotalToolDurationMs = decimals[3],
            CompactMetadata = compact,
            PermissionDecision = permission,
            QuotaLimits = quota,
            ModelUsage = modelUsage,
            Message = message,
        };
    }

    /// <summary>
    /// Decodes a message body.
    /// </summary>
    /// <param name="reader">The reader positioned on the message body.</param>
    /// <param name="lookup">Resolves a string table index to its string (<see langword="null"/> for index 0).</param>
    /// <param name="dataLength">The total size of the data being decoded, used to reject impossible counts.</param>
    /// <returns>The decoded message body.</returns>
    private static MessageContent ReadMessage(BinaryReader reader, Func<int, string?> lookup, int dataLength)
    {
        var flags = (MessageFlags)reader.Read7BitEncodedInt();
        var role = lookup(reader.Read7BitEncodedInt()) ?? "";

        var toolCount = ReadCount(reader, dataLength);
        var toolNames = new string[toolCount];
        for (var i = 0; i < toolCount; i++)
        {
            toolNames[i] = lookup(reader.Read7BitEncodedInt()) ?? "";
        }

        var toolResults = 0;
        var toolResultErrors = 0;
        var thinking = 0;
        var text = 0;
        if (flags.HasFlag(MessageFlags.Content))
        {
            toolResults = reader.Read7BitEncodedInt();
            toolResultErrors = reader.Read7BitEncodedInt();
            thinking = reader.Read7BitEncodedInt();
            text = reader.Read7BitEncodedInt();
        }

        var id = flags.HasFlag(MessageFlags.Id) ? lookup(reader.Read7BitEncodedInt()) : null;
        var model = flags.HasFlag(MessageFlags.Model) ? lookup(reader.Read7BitEncodedInt()) : null;
        var stopReason = flags.HasFlag(MessageFlags.StopReason) ? lookup(reader.Read7BitEncodedInt()) : null;

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

            string? serviceTier = null;
            string? speed = null;
            OutputTokensDetails? outputDetails = null;
            ServerToolUse? serverToolUse = null;
            if (flags.HasFlag(MessageFlags.UsageExtras))
            {
                var extras = (UsageExtraFlags)reader.Read7BitEncodedInt();
                if (extras.HasFlag(UsageExtraFlags.ServiceTier))
                {
                    serviceTier = lookup(reader.Read7BitEncodedInt());
                }

                if (extras.HasFlag(UsageExtraFlags.Speed))
                {
                    speed = lookup(reader.Read7BitEncodedInt());
                }

                if (extras.HasFlag(UsageExtraFlags.OutputDetails))
                {
                    outputDetails = new() { ThinkingTokens = reader.Read7BitEncodedInt64() };
                }

                if (extras.HasFlag(UsageExtraFlags.ServerToolUse))
                {
                    serverToolUse = new()
                    {
                        WebSearchRequests = reader.Read7BitEncodedInt64(),
                        WebFetchRequests = reader.Read7BitEncodedInt64(),
                    };
                }
            }

            usage = new()
            {
                InputTokens = input,
                OutputTokens = output,
                CacheReadInputTokens = cacheRead,
                CacheCreationInputTokens = cacheCreation,
                CacheCreation = detail,
                ServiceTier = serviceTier,
                Speed = speed,
                OutputTokensDetails = outputDetails,
                ServerToolUse = serverToolUse,
            };
        }

        MessageDiagnostics? diagnostics = null;
        if (flags.HasFlag(MessageFlags.CacheMiss))
        {
            diagnostics = new()
            {
                CacheMissReason = new()
                {
                    Type = lookup(reader.Read7BitEncodedInt()),
                    CacheMissedInputTokens = reader.Read7BitEncodedInt64(),
                },
            };
        }

        var content = toolCount + toolResults + toolResultErrors + thinking + text == 0
            ? ContentSummary.Empty
            : new ContentSummary
            {
                ToolUseCount = toolCount,
                ToolNames = toolNames,
                ToolResultCount = toolResults,
                ToolResultErrorCount = toolResultErrors,
                ThinkingBlockCount = thinking,
                TextBlockCount = text,
            };

        return new()
        {
            Role = role,
            Content = content,
            Id = id,
            Model = model,
            StopReason = stopReason,
            Usage = usage,
            Diagnostics = diagnostics,
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
    /// <typeparam name="T">The flag enum type, backed by <see cref="int"/>.</typeparam>
    /// <param name="flag">The flag.</param>
    /// <param name="condition">Whether the flag applies.</param>
    /// <returns><paramref name="flag"/> or the zero value.</returns>
    public static T Only<T>(this T flag, bool condition) where T : struct, Enum
    {
        return condition ? flag : default;
    }
}
