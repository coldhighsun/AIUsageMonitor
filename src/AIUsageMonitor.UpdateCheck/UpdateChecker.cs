using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.UpdateCheck;

/// <summary>
/// Checks GitHub's "latest release" API for a newer published version of the application.
/// </summary>
public sealed class UpdateChecker : IUpdateChecker
{
    /// <summary>
    /// The GitHub page users should visit to download the latest release.
    /// </summary>
    public const string ReleaseUrl = $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases/latest";

    private const string ReleasesApiUrl = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";
    private const string RepositoryName = "AIUsageMonitor";
    private const string RepositoryOwner = "coldhighsun";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly ILogger<UpdateChecker> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateChecker"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client used to call the GitHub API.</param>
    /// <param name="logger">The logger used to record failed update checks.</param>
    public UpdateChecker(HttpClient httpClient, ILogger<UpdateChecker> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var noUpdate = new UpdateCheckResult(false, null, ReleaseUrl);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue(RepositoryName, "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _httpClient.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Update check failed with status {StatusCode}", response.StatusCode);
                return noUpdate;
            }

            var release = await response.Content.ReadFromJsonAsync(
                UpdateCheckJsonContext.Default.GitHubReleaseResponse, cts.Token);

            var latestVersion = VersionParser.Parse(release?.TagName);
            var currentVersion = AppVersion.GetCurrent();

            if (latestVersion is null || currentVersion is null)
            {
                return noUpdate;
            }

            return latestVersion > currentVersion
                ? new UpdateCheckResult(true, latestVersion.ToString(), ReleaseUrl)
                : noUpdate;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Update check could not reach GitHub");
            return noUpdate;
        }
    }
}