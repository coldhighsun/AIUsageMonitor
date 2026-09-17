using System.Text.RegularExpressions;

namespace AIUsageMonitor.UpdateCheck;

/// <summary>
/// Parses loose semantic-version strings (an optional leading <c>v</c>, an optional
/// pre-release/build-metadata suffix such as MinVer's <c>+commitsha</c>) into a plain
/// <see cref="System.Version"/> usable for comparison.
/// </summary>
internal static partial class VersionParser
{
    /// <summary>
    /// Parses the leading <c>major.minor.patch</c> portion of <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The version string to parse, e.g. <c>v1.2.3</c> or <c>1.2.3-preview.1+abcdef</c>.</param>
    /// <returns>The parsed version, or <see langword="null"/> if <paramref name="value"/> is not a recognizable version.</returns>
    public static Version? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = CoreVersionRegex().Match(value);
        return match.Success && Version.TryParse(match.Value, out var version) ? version : null;
    }

    /// <summary>
    /// Parses the <c>major.minor.patch[-prerelease]</c> portion of <paramref name="value"/>, stripping
    /// only MinVer's <c>+commitsha</c> build-metadata suffix (if present) while preserving any
    /// pre-release label such as <c>-alpha.1</c>.
    /// </summary>
    /// <param name="value">The version string to parse, e.g. <c>v1.2.3-beta.1+abcdef</c>.</param>
    /// <returns>The version with pre-release label but no build metadata, or <see langword="null"/> if
    /// <paramref name="value"/> is not a recognizable version.</returns>
    public static string? ParseDisplayString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = DisplayVersionRegex().Match(value);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex CoreVersionRegex();

    [GeneratedRegex(@"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?")]
    private static partial Regex DisplayVersionRegex();
}