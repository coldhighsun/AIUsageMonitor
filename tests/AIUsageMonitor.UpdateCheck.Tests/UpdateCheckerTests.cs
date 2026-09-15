using System.Net;
using AIUsageMonitor.UpdateCheck;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIUsageMonitor.UpdateCheck.Tests;

public class UpdateCheckerTests
{
    [Fact]
    public async Task CheckForUpdateAsync_NewerTagPublished_ReturnsUpdateAvailable()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK, """{"tag_name": "v99.0.0"}""");
        var checker = new UpdateChecker(new HttpClient(handler), NullLogger<UpdateChecker>.Instance);

        var result = await checker.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("99.0.0", result.LatestVersion);
        Assert.Equal(AIUsageMonitor.UpdateCheck.UpdateChecker.ReleaseUrl, result.ReleaseUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsync_NoCurrentVersionKnown_ReturnsNoUpdateAvailable()
    {
        // AppVersion.GetCurrent() returns null in the test host (no AssemblyInformationalVersion
        // attribute set by MinVer during a test run), so a real tag always compares as "no update".
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK, """{"tag_name": "v1.0.0"}""");
        var checker = new UpdateChecker(new HttpClient(handler), NullLogger<UpdateChecker>.Instance);

        var result = await checker.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.LatestVersion);
    }

    [Fact]
    public async Task CheckForUpdateAsync_HttpRequestFails_ReturnsNoUpdateAvailable()
    {
        var handler = new StubHttpMessageHandler(exception: new HttpRequestException("network down"));
        var checker = new UpdateChecker(new HttpClient(handler), NullLogger<UpdateChecker>.Instance);

        var result = await checker.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.LatestVersion);
    }

    [Fact]
    public async Task CheckForUpdateAsync_NonSuccessStatusCode_ReturnsNoUpdateAvailable()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable, string.Empty);
        var checker = new UpdateChecker(new HttpClient(handler), NullLogger<UpdateChecker>.Instance);

        var result = await checker.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
    }

    private sealed class StubHttpMessageHandler(
        HttpStatusCode statusCode = HttpStatusCode.OK, string content = "", Exception? exception = null)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (exception is not null)
            {
                throw exception;
            }

            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            });
        }
    }
}
