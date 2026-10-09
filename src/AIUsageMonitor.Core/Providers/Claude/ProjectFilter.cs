using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Decides whether a session belongs to a project the user asked for by name.
/// </summary>
internal static class ProjectFilter
{
    /// <summary>
    /// Determines whether a session matches a project filter. The text is matched case-insensitively as a substring of the
    /// project's working directory or of its folder name under <c>projects/</c> (an encoded path, so it also matches
    /// when no working directory was recorded), with <c>\</c> and <c>/</c> treated alike.
    /// </summary>
    /// <param name="session">The session to test.</param>
    /// <param name="text">The text to look for; every session matches when it is <see langword="null"/> or blank.</param>
    /// <returns><see langword="true"/> when the session matches.</returns>
    public static bool Matches(SessionUsage session, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var needle = Normalize(text.Trim());
        return (session.ProjectPath is not null && Normalize(session.ProjectPath).Contains(needle, StringComparison.OrdinalIgnoreCase))
            || session.ProjectKey.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Makes path separators uniform.
    /// </summary>
    /// <param name="path">The path or text to normalize.</param>
    /// <returns>The text with every backslash replaced by a forward slash.</returns>
    private static string Normalize(string path)
    {
        return path.Replace('\\', '/');
    }
}
