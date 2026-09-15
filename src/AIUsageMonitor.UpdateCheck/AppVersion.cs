using System.Reflection;

namespace AIUsageMonitor.UpdateCheck;

/// <summary>
/// Reads the running application's version, as embedded by MinVer at build time.
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// Gets the current application version, or <see langword="null"/> if it could not be determined
    /// (e.g. running from an unpublished build with no informational version attribute).
    /// </summary>
    /// <returns>The current semantic version, with any MinVer commit-hash suffix stripped.</returns>
    public static Version? GetCurrent()
    {
        var informationalVersion = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return VersionParser.Parse(informationalVersion);
    }
}