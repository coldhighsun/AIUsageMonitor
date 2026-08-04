namespace AIUsageMonitor.Core.Providers.Claude;

public sealed class ClaudeDataLocator
{
    private readonly string _claudeDir;

    public ClaudeDataLocator(string? claudeDir = null)
    {
        _claudeDir = claudeDir
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    }

    public string ClaudeDir => _claudeDir;

    public string StatsCachePath => Path.Combine(_claudeDir, "stats-cache.json");

    public string HistoryPath => Path.Combine(_claudeDir, "history.jsonl");

    public string ProjectsDir => Path.Combine(_claudeDir, "projects");

    public IReadOnlyList<string> GetSessionFiles()
    {
        var projectsDir = ProjectsDir;
        if (!Directory.Exists(projectsDir))
        {
            return [];
        }

        var files = new List<string>();
        foreach (var projectDir in Directory.EnumerateDirectories(projectsDir))
        {
            foreach (var file in Directory.EnumerateFiles(projectDir, "*.jsonl"))
            {
                files.Add(file);
            }
        }

        return files;
    }

    public IReadOnlyList<(string EncodedName, string FullPath)> GetProjectDirectories()
    {
        var projectsDir = ProjectsDir;
        if (!Directory.Exists(projectsDir))
        {
            return [];
        }

        return Directory.EnumerateDirectories(projectsDir)
            .Select(d => (Path.GetFileName(d), d))
            .ToList();
    }
}
