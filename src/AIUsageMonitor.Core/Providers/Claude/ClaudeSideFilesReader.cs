using AIUsageMonitor.Core.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Reads the small files Claude Code keeps next to the transcripts: the files of running processes, the metadata of
/// sub-agents, and a metadata-only inventory of the folder. Everything is best effort: an unreadable or malformed file is
/// skipped, and no conversation text, task description or file content is read.
/// </summary>
/// <param name="locator">Locates the Claude Code data folder.</param>
/// <param name="logger">The logger used to record files that could not be read.</param>
public sealed class ClaudeSideFilesReader(ClaudeDataLocator locator, ILogger<ClaudeSideFilesReader> logger)
{
    /// <summary>
    /// The suffix of a sub-agent's metadata file.
    /// </summary>
    private const string MetaSuffix = ".meta.json";

    /// <summary>
    /// The suffix of a sub-agent's request-prefix file.
    /// </summary>
    private const string PrefixSuffix = ".prefix.json";

    /// <summary>
    /// The prefix of the file name of everything a sub-agent writes.
    /// </summary>
    private const string AgentPrefix = "agent-";

    /// <summary>
    /// How far a process's actual start time may differ from the one Claude Code recorded and still be the same process.
    /// </summary>
    private static readonly TimeSpan ProcessStartTolerance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reads the files of the Claude Code processes under the <c>sessions</c> directory.
    /// </summary>
    /// <returns>One entry per readable file, most recently updated first.</returns>
    public IReadOnlyList<ActiveSession> GetActiveSessions()
    {
        var result = new List<ActiveSession>();
        foreach (var file in EnumerateFiles(locator.SessionsDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            using var document = TryRead(file);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var root = document.RootElement;
            var pid = GetLong(root, "pid") is { } value and > 0 and <= int.MaxValue ? (int)value : -1;
            if (pid <= 0)
            {
                continue;
            }

            result.Add(new(
                pid,
                GetString(root, "sessionId"),
                GetString(root, "cwd"),
                GetString(root, "name"),
                GetString(root, "status"),
                GetString(root, "kind"),
                GetString(root, "entrypoint"),
                GetString(root, "version"),
                GetTime(root, "startedAt"),
                GetTime(root, "updatedAt"),
                IsRunning(pid, GetText(root, "procStart"))));
        }

        return result.OrderByDescending(s => s.UpdatedAt).ThenBy(s => s.Pid).ToList();
    }

    /// <summary>
    /// Reads the metadata files next to the sub-agent transcripts.
    /// </summary>
    /// <returns>One entry per sub-agent that has a metadata or request-prefix file, ordered by session and agent id.</returns>
    public IReadOnlyList<SubagentInfo> GetSubagents()
    {
        var agents = new Dictionary<(string? Session, string Agent), SubagentBuilder>();

        foreach (var file in EnumerateFiles(locator.ProjectsDir, "agent-*.json", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            var isMeta = name.EndsWith(MetaSuffix, StringComparison.Ordinal);
            if (!isMeta && !name.EndsWith(PrefixSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(file);
            if (!string.Equals(Path.GetFileName(directory), "subagents", StringComparison.Ordinal))
            {
                continue;
            }

            var suffixLength = isMeta ? MetaSuffix.Length : PrefixSuffix.Length;
            var agentId = name[AgentPrefix.Length..^suffixLength];
            var session = Path.GetFileName(Path.GetDirectoryName(directory));

            // A prefix file can hold the request prefix itself, so only its small state object is ever materialized.
            using var document = isMeta ? TryRead(file) : TryReadStateObject(file);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!agents.TryGetValue((session, agentId), out var builder))
            {
                builder = new SubagentBuilder();
                agents[(session, agentId)] = builder;
            }

            if (isMeta)
            {
                builder.ReadMeta(document.RootElement);
            }
            else
            {
                builder.ReadPrefixState(document.RootElement);
            }
        }

        return agents
            .OrderBy(pair => pair.Key.Session, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Agent, StringComparer.Ordinal)
            .Select(pair => pair.Value.ToInfo(pair.Key.Agent, pair.Key.Session))
            .ToList();
    }

    /// <summary>
    /// Summarizes what the Claude Code folder holds, reading file metadata only.
    /// </summary>
    /// <returns>The inventory.</returns>
    public ClaudeDataInventory GetInventory()
    {
        var transcripts = 0;
        var subagentTranscripts = 0;
        var transcriptBytes = 0L;
        DateTimeOffset? oldest = null;
        DateTimeOffset? newest = null;

        foreach (var file in EnumerateFiles(locator.ProjectsDir, "*.jsonl", SearchOption.AllDirectories))
        {
            var info = TryInfo(file);
            if (info is null)
            {
                continue;
            }

            transcripts++;
            transcriptBytes += info.Length;
            if (Path.GetFileName(Path.GetDirectoryName(file)) == "subagents")
            {
                subagentTranscripts++;
            }

            var written = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            oldest = oldest is null || written < oldest ? written : oldest;
            newest = newest is null || written > newest ? written : newest;
        }

        var historyFiles = 0;
        var historyBytes = 0L;
        foreach (var file in EnumerateFiles(locator.FileHistoryDir, "*", SearchOption.AllDirectories))
        {
            if (TryInfo(file) is { } info)
            {
                historyFiles++;
                historyBytes += info.Length;
            }
        }

        return new(
            locator.GetProjectDirectories().Count,
            transcripts,
            subagentTranscripts,
            transcriptBytes,
            oldest,
            newest,
            EnumerateFiles(locator.PlansDir, "*.md", SearchOption.TopDirectoryOnly).Count(),
            historyFiles,
            historyBytes,
            EnumerateFiles(locator.SessionsDir, "*.json", SearchOption.TopDirectoryOnly).Count(),
            File.Exists(locator.StatsCachePath),
            File.Exists(locator.HistoryPath));
    }

    /// <summary>
    /// Determines whether a process with the given id exists. The id-reuse check needs the recorded start time and is
    /// only made on Windows, where Claude Code records it as a file time; elsewhere a reused id reads as running.
    /// </summary>
    /// <param name="pid">The process id.</param>
    /// <param name="procStart">
    /// The start time Claude Code recorded for the process, as a Windows file time. When it is present and the process
    /// started at a clearly different time, the id has been reused by an unrelated process.
    /// </param>
    /// <returns><see langword="true"/> when the process exists.</returns>
    internal static bool IsRunning(int pid, string? procStart = null)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            if (OperatingSystem.IsWindows()
                && long.TryParse(procStart, NumberStyles.None, CultureInfo.InvariantCulture, out var recorded)
                && recorded > 0)
            {
                var actual = process.StartTime.ToFileTimeUtc();

                return Math.Abs(actual - recorded) <= ProcessStartTolerance.Ticks;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // No such process, or it exited while it was being inspected.
            return false;
        }
        catch (Exception ex) when (ex is NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // The process exists but cannot be inspected (e.g. access denied): it is not known to be gone.
            return true;
        }
    }

    /// <summary>
    /// Lists files, treating a missing or unreadable directory as empty.
    /// </summary>
    /// <param name="directory">The directory.</param>
    /// <param name="pattern">The file name pattern.</param>
    /// <param name="option">Whether to descend into subdirectories.</param>
    /// <returns>The paths of the matching files.</returns>
    private IEnumerable<string> EnumerateFiles(string directory, string pattern, SearchOption option)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        // Streamed rather than collected, because folders such as file-history can hold tens of thousands of files.
        IEnumerator<string> files;
        try
        {
            // An inaccessible subdirectory is skipped instead of ending the whole listing.
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = option == SearchOption.AllDirectories,
                MatchType = MatchType.Win32,
            };
            files = Directory.EnumerateFiles(directory, pattern, options).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not list {Directory}", directory);

            yield break;
        }

        using (files)
        {
            while (true)
            {
                try
                {
                    if (!files.MoveNext())
                    {
                        break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Could not list {Directory}", directory);

                    break;
                }

                yield return files.Current;
            }
        }
    }

    /// <summary>
    /// Reads the metadata of a file, treating any failure as no information.
    /// </summary>
    /// <param name="file">The path of the file.</param>
    /// <returns>The file information, or <see langword="null"/> when the file cannot be inspected.</returns>
    private static FileInfo? TryInfo(string file)
    {
        try
        {
            var info = new FileInfo(file);

            return info.Exists ? info : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a small JSON file, treating any failure as no document.
    /// </summary>
    /// <param name="file">The path of the file.</param>
    /// <returns>The parsed document, or <see langword="null"/> when the file cannot be read or parsed.</returns>
    private JsonDocument? TryRead(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            return JsonDocument.Parse(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogDebug(ex, "Could not read {File}", file);

            return null;
        }
    }

    /// <summary>
    /// Reads only the top-level <c>state</c> object of a JSON file, skipping everything else without building a document for it.
    /// </summary>
    /// <param name="file">The path of the file.</param>
    /// <returns>A document holding the <c>state</c> object, or <see langword="null"/> when it is absent or the file cannot be read.</returns>
    private JsonDocument? TryReadStateObject(string file)
    {
        try
        {
            byte[] bytes;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }

            var reader = new Utf8JsonReader(bytes);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isState = reader.ValueTextEquals("state"u8);
                reader.Read();
                if (isState)
                {
                    return reader.TokenType == JsonTokenType.StartObject ? JsonDocument.ParseValue(ref reader) : null;
                }

                reader.Skip();
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogDebug(ex, "Could not read {File}", file);

            return null;
        }
    }

    /// <summary>
    /// Gets a string property, or <see langword="null"/> when it is missing or not a string.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The value.</returns>
    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>
    /// Gets a property written as a string or as a number (Claude Code writes <c>procStart</c> as a string), as text.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The text, or <see langword="null"/> when the property is missing or neither a string nor a number.</returns>
    private static string? GetText(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;
    }

    /// <summary>
    /// Gets a whole-number property, or <see langword="null"/> when it is missing or not a number.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The value.</returns>
    private static long? GetLong(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;
    }

    /// <summary>
    /// Gets a boolean property, or <see langword="null"/> when it is missing or not a boolean.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The value.</returns>
    private static bool? GetBool(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    /// <summary>
    /// Gets a time property written as Unix milliseconds or as an ISO string.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The time, or <see langword="null"/> when it is missing or unusable.</returns>
    private static DateTimeOffset? GetTime(JsonElement element, string name)
    {
        if (GetLong(element, name) is { } milliseconds)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return GetString(element, name) is { } text && DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Collects what a sub-agent's two small files say about it.
    /// </summary>
    private sealed class SubagentBuilder
    {
        /// <summary>
        /// The type of agent.
        /// </summary>
        private string? _agentType;

        /// <summary>
        /// The spawn depth.
        /// </summary>
        private int? _spawnDepth;

        /// <summary>
        /// The model of the agent's requests.
        /// </summary>
        private string? _model;

        /// <summary>
        /// Whether the agent ran in fast mode.
        /// </summary>
        private bool? _fastMode;

        /// <summary>
        /// Whether the agent used the 1-hour cache lifetime.
        /// </summary>
        private bool? _oneHourCache;

        /// <summary>
        /// The reasoning effort.
        /// </summary>
        private string? _effort;

        /// <summary>
        /// The thinking mode.
        /// </summary>
        private string? _thinkingMode;

        /// <summary>
        /// The number of API calls.
        /// </summary>
        private long? _callCount;

        /// <summary>
        /// Reads the metadata file; the description it holds is not read.
        /// </summary>
        /// <param name="root">The root object.</param>
        public void ReadMeta(JsonElement root)
        {
            _agentType = GetString(root, "agentType");
            _spawnDepth = GetLong(root, "spawnDepth") is { } depth and >= 0 and <= int.MaxValue ? (int)depth : null;
        }

        /// <summary>
        /// Reads the <c>state</c> summary of the request-prefix file.
        /// </summary>
        /// <param name="state">The <c>state</c> object.</param>
        public void ReadPrefixState(JsonElement state)
        {
            _model = GetString(state, "model");
            _fastMode = GetBool(state, "fastMode");
            _oneHourCache = GetBool(state, "is1hCacheTTL");
            _effort = GetString(state, "effortValue");
            _thinkingMode = GetString(state, "thinkingMode");
            _callCount = GetLong(state, "callCount");
        }

        /// <summary>
        /// Builds the sub-agent record.
        /// </summary>
        /// <param name="agentId">The sub-agent id.</param>
        /// <param name="sessionId">The session that spawned it.</param>
        /// <returns>The record.</returns>
        public SubagentInfo ToInfo(string agentId, string? sessionId)
        {
            return new(agentId, sessionId, _agentType, _spawnDepth, _model, _fastMode, _oneHourCache, _effort, _thinkingMode, _callCount);
        }
    }
}
