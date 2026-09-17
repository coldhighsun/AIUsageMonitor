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

    /// <summary>
    /// Gets the current application version as a display string, including any pre-release label
    /// (e.g. <c>-alpha.1</c>) but excluding MinVer's <c>+commitsha</c> build metadata.
    /// </summary>
    /// <returns>The current version string, or <see langword="null"/> if it could not be determined.</returns>
    public static string? GetCurrentDisplayString()
    {
        var informationalVersion = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return VersionParser.ParseDisplayString(informationalVersion);
    }
}