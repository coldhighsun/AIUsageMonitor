using AIUsageMonitor.Core.Models;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Asks Anthropic's account API (<c>GET /api/oauth/usage</c>, undocumented) for the exact usage percentages and reset
/// times of the signed-in account, with Claude Code's own OAuth token from <c>.credentials.json</c>. The token is read
/// from disk for each request, used for that one request and never stored, logged or written anywhere. Every failure
/// is reported as an <see cref="OnlineLimitsFailure"/>; nothing is thrown for a network, sign-in or format problem.
/// </summary>
/// <param name="locator">Locates the Claude Code data folder, where the sign-in is kept.</param>
/// <param name="http">The HTTP client used for the request (injected so it can be faked).</param>
/// <param name="logger">The logger used to record why a request failed (never the token).</param>
public sealed class ClaudeOAuthUsageClient(ClaudeDataLocator locator, HttpClient http, ILogger<ClaudeOAuthUsageClient> logger)
{
    /// <summary>The address of the usage endpoint.</summary>
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>The value of the <c>anthropic-beta</c> header the OAuth endpoints expect.</summary>
    public const string BetaHeader = "oauth-2025-04-20";

    /// <summary>The <c>User-Agent</c> sent with the request: the tool's name and its version (MinVer), e.g. <c>aimon/1.2.3</c>.</summary>
    internal static readonly string UserAgent = BuildUserAgent();

    /// <summary>How long a request may take before it is given up on.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private static string BuildUserAgent()
    {
        var assembly = typeof(ClaudeOAuthUsageClient).Assembly;
        var version = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3);

        // Drop the build metadata after '+' (the commit hash); keep only characters a product version may hold.
        version = version?.Split('+')[0];
        return string.IsNullOrWhiteSpace(version) || version.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            ? "aimon"
            : "aimon/" + version;
    }

    /// <summary>
    /// The outcome of one request.
    /// </summary>
    /// <param name="Limits">The quotas, when the request succeeded.</param>
    /// <param name="Failure">Why there are none; <see cref="OnlineLimitsFailure.None"/> on success.</param>
    /// <param name="RetryAfter">The wait the server asked for with a 429, if it named one.</param>
    internal sealed record Outcome(OnlineLimits? Limits, OnlineLimitsFailure Failure, TimeSpan? RetryAfter = null);

    /// <summary>
    /// Gets a one-way hash of the id of the account Claude Code is signed in to, so a kept reading can be told apart from
    /// another account's without naming either.
    /// </summary>
    /// <returns>The hash, or an empty string when the account cannot be told.</returns>
    public string GetAccountHash()
    {
        var parent = Path.GetDirectoryName(locator.ClaudeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var candidates = new[]
        {
            parent is null ? null : Path.Combine(parent, ".claude.json"),
            Path.Combine(locator.ClaudeDir, ".claude.json"),
        };

        foreach (var path in candidates.OfType<string>())
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("oauthAccount", out var account)
                    && account.ValueKind == JsonValueKind.Object
                    && account.TryGetProperty("accountUuid", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                        "aimon claude account " + id.GetString()!.Trim().ToLowerInvariant())))[..32];
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                logger.LogDebug(ex, "Could not read the account record {Path}", path);
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Asks the API for the account's quotas.
    /// </summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The quotas, or the reason there are none.</returns>
    internal async Task<Outcome> FetchAsync(CancellationToken ct)
    {
        var token = ReadToken();
        if (token is null)
        {
            return new(null, OnlineLimitsFailure.NotSignedIn);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("anthropic-beta", BetaHeader);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);

            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return new(null, OnlineLimitsFailure.Rejected);
                case HttpStatusCode.TooManyRequests:
                    return new(null, OnlineLimitsFailure.RateLimited, ParseRetryAfter(response.Headers.RetryAfter));
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("The usage endpoint answered {Status}", (int)response.StatusCode);
                return new(null, OnlineLimitsFailure.Unavailable);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            var limits = Parse(document.RootElement);
            return limits is null
                ? new(null, OnlineLimitsFailure.Unavailable)
                : new(limits, OnlineLimitsFailure.None);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Reading the online limits failed");
            return new(null, OnlineLimitsFailure.Unavailable);
        }
    }

    /// <summary>
    /// Reads the quotas out of the API's answer. Two shapes are understood: the flat one
    /// (<c>five_hour</c> / <c>seven_day</c>, each with <c>utilization</c> and <c>resets_at</c>) and the list one
    /// (<c>limits[]</c> with <c>kind</c>, <c>percent</c> and <c>resets_at</c>). A missing or negative figure is left out,
    /// never replaced by a zero.
    /// </summary>
    /// <param name="root">The root of the answer.</param>
    /// <returns>The quotas, or <see langword="null"/> when the answer holds neither.</returns>
    internal static OnlineLimits? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var session = ReadQuota(root, "five_hour", "utilization");
        var week = ReadQuota(root, "seven_day", "utilization");

        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray())
            {
                if (limit.ValueKind != JsonValueKind.Object || !limit.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                switch (kind.GetString())
                {
                    case "session":
                        session ??= ReadQuota(limit, null, "percent");
                        break;
                    case "weekly_all":
                        week ??= ReadQuota(limit, null, "percent");
                        break;
                }
            }
        }

        return session is null && week is null ? null : new(session, week);
    }

    private static OnlineQuota? ReadQuota(JsonElement parent, string? property, string percentKey)
    {
        var element = parent;
        if (property is not null && (!parent.TryGetProperty(property, out element) || element.ValueKind != JsonValueKind.Object))
        {
            return null;
        }

        if (!element.TryGetProperty(percentKey, out var percent)
            || percent.ValueKind != JsonValueKind.Number
            || !percent.TryGetDouble(out var value)
            || !double.IsFinite(value)
            || value < 0)
        {
            return null;
        }

        DateTimeOffset? resetsAt = null;
        if (element.TryGetProperty("resets_at", out var reset) && reset.ValueKind == JsonValueKind.String && reset.TryGetDateTimeOffset(out var parsed))
        {
            resetsAt = parsed;
        }

        return new(value, resetsAt);
    }

    private static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? header)
    {
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }

    /// <summary>
    /// Reads Claude Code's access token. It is held only for the request that uses it.
    /// </summary>
    /// <returns>The token, or <see langword="null"/> when there is no usable sign-in.</returns>
    private string? ReadToken()
    {
        try
        {
            var path = Path.Combine(locator.ClaudeDir, ".credentials.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                && oauth.ValueKind == JsonValueKind.Object
                && oauth.TryGetProperty("accessToken", out var token)
                && token.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(token.GetString()))
            {
                return token.GetString();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogDebug(ex, "Could not read the Claude Code sign-in");
        }

        return null;
    }
}
