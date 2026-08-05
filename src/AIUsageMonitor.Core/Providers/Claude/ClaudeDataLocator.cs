namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Locates Claude Code data files and directories on disk, such as history logs,
/// project session files, and the stats cache.
/// </summary>
/// <param name="claudeDir">
/// Optional path to the Claude data directory. When <see langword="null"/>, defaults to
/// the <c>.claude</c> folder under the current user's profile directory.
/// </param>
public sealed class ClaudeDataLocator(string? claudeDir = null)
{
    private readonly string _claudeDir =
        claudeDir ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>
    /// Gets the root Claude data directory (e.g. <c>%USERPROFILE%\.claude</c>).
    /// </summary>
    public string ClaudeDir => _claudeDir;

    /// <summary>
    /// Gets the full path to the <c>history.jsonl</c> file containing Claude command history.
    /// </summary>
    public string HistoryPath => Path.Combine(_claudeDir, "history.jsonl");

    /// <summary>
    /// Gets the full path to the <c>projects</c> directory containing per-project session data.
    /// </summary>
    public string ProjectsDir => Path.Combine(_claudeDir, "projects");

    /// <summary>
    /// Gets the full path to the <c>stats-cache.json</c> file used to cache computed usage statistics.
    /// </summary>
    public string StatsCachePath => Path.Combine(_claudeDir, "stats-cache.json");

    /// <summary>
    /// Enumerates the project directories under <see cref="ProjectsDir"/>.
    /// </summary>
    /// <returns>
    /// A list of tuples containing each project's encoded directory name and its full path,
    /// or an empty list if <see cref="ProjectsDir"/> does not exist.
    /// </returns>
    public IReadOnlyList<(string EncodedName, string FullPath)> GetProjectDirectories()
    {
        if (!Directory.Exists(ProjectsDir))
        {
            return [];
        }

        return Directory.EnumerateDirectories(ProjectsDir)
            .Select(d => (Path.GetFileName(d), d))
            .ToList();
    }

    /// <summary>
    /// Finds all session log files (<c>*.jsonl</c>) recursively under <see cref="ProjectsDir"/>.
    /// </summary>
    /// <returns>
    /// A list of full paths to session files, or an empty list if <see cref="ProjectsDir"/> does not exist.
    /// </returns>
    public IReadOnlyList<string> GetSessionFiles()
    {
        if (!Directory.Exists(ProjectsDir))
        {
            return [];
        }

        return Directory.GetFiles(ProjectsDir, "*.jsonl", SearchOption.AllDirectories);
    }
}