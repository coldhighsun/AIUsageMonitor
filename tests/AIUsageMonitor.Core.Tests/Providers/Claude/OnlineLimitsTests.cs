using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

/// <summary>
/// Tests for the online quota reading: the response parser, the request, the 429 pause, the kept reading and the service
/// that ties them together. No test touches the network; the HTTP handler is a fake.
/// </summary>
public sealed class OnlineLimitsTests : IDisposable
{
    private const string Token = "secret-test-token";

    private static readonly DateTimeOffset Now = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"online-limits-{Guid.NewGuid()}");

    private readonly string _claudeDir;

    private readonly string _cachePath;

    /// <summary>
    /// Creates a folder standing in for the user profile, with a Claude data folder and a sign-in in it.
    /// </summary>
    public OnlineLimitsTests()
    {
        _claudeDir = Path.Combine(_root, ".claude");
        _cachePath = Path.Combine(_root, "cache", "online-limits.json");
        Directory.CreateDirectory(_claudeDir);
        SignIn("account-a");
    }

    /// <summary>
    /// Deletes the folder.
    /// </summary>
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void SignIn(string account)
    {
        File.WriteAllText(Path.Combine(_claudeDir, ".credentials.json"), "{\"claudeAiOauth\":{\"accessToken\":\"" + Token + "\"}}");
        File.WriteAllText(Path.Combine(_root, ".claude.json"), "{\"oauthAccount\":{\"accountUuid\":\"" + account + "\"}}");
    }

    private static string Body(double session, double week) => JsonSerializer.Serialize(new
    {
        five_hour = new { utilization = session, resets_at = Now.AddHours(3) },
        seven_day = new { utilization = week, resets_at = Now.AddDays(4) },
    });

    private static OnlineLimits? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ClaudeOAuthUsageClient.Parse(document.RootElement);
    }

    /// <summary>
    /// The flat answer gives both quotas with their reset times.
    /// </summary>
    [Fact]
    public void Parse_FlatShape_ReadsBothQuotas()
    {
        var limits = Parse(Body(12.5, 33));

        Assert.NotNull(limits);
        Assert.Equal(12.5, limits.Session!.Percent);
        Assert.Equal(Now.AddHours(3), limits.Session.ResetsAt);
        Assert.Equal(33, limits.Week!.Percent);
        Assert.Equal(Now.AddDays(4), limits.Week.ResetsAt);
    }

    /// <summary>
    /// The list answer is understood too, and the flat one wins when both are present.
    /// </summary>
    [Fact]
    public void Parse_ListShape_ReadsSessionAndWeeklyAll()
    {
        var limits = Parse("""
            {"limits":[
              {"kind":"session","percent":40,"resets_at":"2026-10-20T13:00:00Z"},
              {"kind":"weekly_all","percent":7,"resets_at":"2026-10-24T10:00:00Z"},
              {"kind":"weekly_scoped","percent":99,"scope":{"model":{"display_name":"Opus"}}}]}
            """);

        Assert.Equal(40, limits!.Session!.Percent);
        Assert.Equal(7, limits.Week!.Percent);

        var both = Parse("""{"five_hour":{"utilization":1},"limits":[{"kind":"session","percent":50}]}""");
        Assert.Equal(1, both!.Session!.Percent);
    }

    /// <summary>
    /// A missing, negative or non-numeric figure is left out, never turned into a zero; an answer with nothing usable is null.
    /// </summary>
    [Theory]
    [InlineData("""{"five_hour":{"utilization":-1}}""")]
    [InlineData("""{"five_hour":{"utilization":"12"}}""")]
    [InlineData("""{"five_hour":null,"seven_day":{}}""")]
    [InlineData("""{"limits":[{"kind":"session"}]}""")]
    [InlineData("""[1,2]""")]
    public void Parse_UnusableFigures_ReturnsNull(string json) => Assert.Null(Parse(json));

    /// <summary>
    /// One good quota is enough, and a bad reset time only drops the reset time.
    /// </summary>
    [Fact]
    public void Parse_OneQuotaAndBadReset_KeepsWhatIsUsable()
    {
        var limits = Parse("""{"five_hour":{"utilization":0,"resets_at":"soon"},"seven_day":{"utilization":-3}}""");

        Assert.Equal(0, limits!.Session!.Percent);
        Assert.Null(limits.Session.ResetsAt);
        Assert.Null(limits.Week);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = answer;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Answer(request));
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage TooMany(int? retryAfterSeconds)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(seconds));
        }

        return response;
    }

    private ClaudeOAuthUsageClient Client(Handler handler)
        => new(new ClaudeDataLocator(_claudeDir), new HttpClient(handler), NullLogger<ClaudeOAuthUsageClient>.Instance);

    /// <summary>
    /// The request goes to the usage endpoint with the bearer token and the beta header, and the answer is parsed.
    /// </summary>
    [Fact]
    public async Task Fetch_Success_SendsTokenAndBetaHeader()
    {
        var handler = new Handler(_ => Ok(Body(10, 20)));

        var outcome = await Client(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(OnlineLimitsFailure.None, outcome.Failure);
        Assert.Equal(10, outcome.Limits!.Session!.Percent);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(ClaudeOAuthUsageClient.UsageUrl, request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);
        Assert.Equal(ClaudeOAuthUsageClient.BetaHeader, Assert.Single(request.Headers.GetValues("anthropic-beta")));
    }

    /// <summary>
    /// Without a sign-in nothing is sent.
    /// </summary>
    [Fact]
    public async Task Fetch_NoCredentials_SendsNothing()
    {
        File.Delete(Path.Combine(_claudeDir, ".credentials.json"));
        var handler = new Handler(_ => Ok(Body(1, 1)));

        var outcome = await Client(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(OnlineLimitsFailure.NotSignedIn, outcome.Failure);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// Each failure kind is reported as its own reason, a 429 carries the server's wait, and nothing is thrown.
    /// </summary>
    [Fact]
    public async Task Fetch_Failures_AreReported()
    {
        var handler = new Handler(_ => new(HttpStatusCode.Unauthorized));
        var client = Client(handler);
        Assert.Equal(OnlineLimitsFailure.Rejected, (await client.FetchAsync(CancellationToken.None)).Failure);

        handler.Answer = _ => new(HttpStatusCode.Forbidden);
        Assert.Equal(OnlineLimitsFailure.Rejected, (await client.FetchAsync(CancellationToken.None)).Failure);

        handler.Answer = _ => TooMany(600);
        var limited = await client.FetchAsync(CancellationToken.None);
        Assert.Equal(OnlineLimitsFailure.RateLimited, limited.Failure);
        Assert.Equal(TimeSpan.FromSeconds(600), limited.RetryAfter);

        handler.Answer = _ => new(HttpStatusCode.InternalServerError);
        Assert.Equal(OnlineLimitsFailure.Unavailable, (await client.FetchAsync(CancellationToken.None)).Failure);

        handler.Answer = _ => Ok("not json");
        Assert.Equal(OnlineLimitsFailure.Unavailable, (await client.FetchAsync(CancellationToken.None)).Failure);

        handler.Answer = _ => Ok("""{"nothing":1}""");
        Assert.Equal(OnlineLimitsFailure.Unavailable, (await client.FetchAsync(CancellationToken.None)).Failure);

        handler.Answer = _ => throw new HttpRequestException("offline");
        Assert.Equal(OnlineLimitsFailure.Unavailable, (await client.FetchAsync(CancellationToken.None)).Failure);
    }

    /// <summary>
    /// The account hash tells accounts apart without containing the id.
    /// </summary>
    [Fact]
    public void GetAccountHash_DiffersByAccountAndHidesTheId()
    {
        var client = Client(new(_ => Ok("{}")));
        var first = client.GetAccountHash();
        SignIn("account-b");
        var second = client.GetAccountHash();

        Assert.Equal(32, first.Length);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("account-a", first);

        File.Delete(Path.Combine(_root, ".claude.json"));
        Assert.Equal(string.Empty, client.GetAccountHash());
    }

    /// <summary>
    /// The wait follows Retry-After (at least 30 seconds, ignored beyond 7 days), otherwise 2, 4, 8, 16, 30 minutes.
    /// </summary>
    [Fact]
    public void Backoff_Delay_FollowsTheRules()
    {
        Assert.Equal(TimeSpan.FromSeconds(600), RateLimitBackoff.Delay(1, TimeSpan.FromSeconds(600), 0));
        Assert.Equal(RateLimitBackoff.MinDelay, RateLimitBackoff.Delay(1, TimeSpan.FromSeconds(1), 0));
        Assert.Equal(RateLimitBackoff.FirstDelay, RateLimitBackoff.Delay(1, TimeSpan.FromDays(8), 0));
        Assert.Equal(RateLimitBackoff.FirstDelay, RateLimitBackoff.Delay(1, null, 0));
        Assert.Equal(TimeSpan.FromMinutes(4), RateLimitBackoff.Delay(2, null, 0));
        Assert.Equal(TimeSpan.FromMinutes(8), RateLimitBackoff.Delay(3, null, 0));
        Assert.Equal(TimeSpan.FromMinutes(16), RateLimitBackoff.Delay(4, null, 0));
        Assert.Equal(TimeSpan.FromMinutes(30), RateLimitBackoff.Delay(5, null, 0));
        Assert.Equal(TimeSpan.FromMinutes(30), RateLimitBackoff.Delay(50, null, 0));
        Assert.Equal(TimeSpan.FromMinutes(2.2), RateLimitBackoff.Delay(1, null, 1));
    }

    /// <summary>
    /// A pause holds until it ends, and a success clears it.
    /// </summary>
    [Fact]
    public void Backoff_PauseHoldsUntilItEndsAndResets()
    {
        var backoff = new RateLimitBackoff();
        Assert.Null(backoff.RetryAt(Now));

        var until = backoff.Fail(Now, TimeSpan.FromMinutes(5), 0);
        Assert.Equal(Now.AddMinutes(5), until);
        Assert.Equal(until, backoff.RetryAt(Now.AddMinutes(4)));
        Assert.Null(backoff.RetryAt(Now.AddMinutes(5)));

        backoff.Reset();
        Assert.Equal(0, backoff.Failures);
        Assert.Null(backoff.RetryAt(Now));
    }

    /// <summary>
    /// The kept reading comes back for the same account, without a token in the file.
    /// </summary>
    [Fact]
    public void Cache_RoundTrip_AndNoToken()
    {
        var cache = new OnlineLimitsCache(_cachePath);
        var limits = new OnlineLimits(new(12, Now.AddHours(2)), new(30, Now.AddDays(2)));
        cache.Save("acct", limits, Now);

        var loaded = cache.Load("acct", Now.AddMinutes(1));

        Assert.Equal(limits, loaded!.Value.Limits);
        Assert.Equal(Now, loaded.Value.ReadAt);
        Assert.DoesNotContain(Token, File.ReadAllText(_cachePath));
    }

    /// <summary>
    /// A reading of another account, too old, from the future, empty, or damaged is a miss; a window that has reset is dropped.
    /// </summary>
    [Fact]
    public void Cache_Misses()
    {
        var cache = new OnlineLimitsCache(_cachePath);
        Assert.Null(cache.Load("acct", Now));

        cache.Save("acct", new(new(12, Now.AddHours(2)), new(30, Now.AddDays(2))), Now);
        Assert.Null(cache.Load("other", Now));
        Assert.Null(cache.Load("", Now));
        Assert.Null(cache.Load("acct", Now + OnlineLimitsCache.MaxAge + TimeSpan.FromSeconds(1)));
        Assert.Null(cache.Load("acct", Now.AddHours(-1)));

        var afterSessionReset = cache.Load("acct", Now.AddHours(3));
        Assert.Null(afterSessionReset!.Value.Limits.Session);
        Assert.Equal(30, afterSessionReset.Value.Limits.Week!.Percent);

        Assert.Null(cache.Load("acct", Now.AddDays(3)));

        File.WriteAllText(_cachePath, "{ damaged");
        Assert.Null(cache.Load("acct", Now));

        cache.Save("acct", new(new(1, null), null), Now);
        cache.Delete();
        Assert.False(File.Exists(_cachePath));
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Current;
    }

    private (ClaudeOnlineLimitsService Service, Handler Handler, Clock Clock) Service()
    {
        var handler = new Handler(_ => Ok(Body(10, 20)));
        var clock = new Clock(Now);
        var service = new ClaudeOnlineLimitsService(Client(handler), new OnlineLimitsCache(_cachePath), clock, () => 0);
        return (service, handler, clock);
    }

    /// <summary>
    /// A live reading is kept for five minutes without asking again, then asked for anew.
    /// </summary>
    [Fact]
    public async Task Service_LiveReading_IsThrottledToOneRequestPerInterval()
    {
        var (service, handler, clock) = Service();

        var first = await service.GetAsync(TestContext.Current.CancellationToken);
        Assert.True(first.IsLive);
        Assert.Equal(Now, first.ReadAt);

        clock.Current = Now.AddMinutes(4);
        Assert.Same(first, await service.GetAsync(TestContext.Current.CancellationToken));
        Assert.Single(handler.Requests);

        clock.Current = Now.AddMinutes(5).AddSeconds(1);
        handler.Answer = _ => Ok(Body(15, 21));
        var second = await service.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(15, second.Limits!.Session!.Percent);
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>
    /// After a 429 no request goes out until the pause ends; the earlier reading shows meanwhile, marked as not live.
    /// </summary>
    [Fact]
    public async Task Service_RateLimited_PausesAndFallsBackToTheEarlierReading()
    {
        var (service, handler, clock) = Service();
        await service.GetAsync(TestContext.Current.CancellationToken);

        clock.Current = Now.AddMinutes(6);
        handler.Answer = _ => TooMany(600);
        var limited = await service.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OnlineLimitsFailure.RateLimited, limited.Failure);
        Assert.True(limited.FromCache);
        Assert.False(limited.IsLive);
        Assert.Equal(10, limited.Limits!.Session!.Percent);
        Assert.Equal(clock.Current.AddSeconds(600), limited.RetryAt);
        var sent = handler.Requests.Count;

        clock.Current = Now.AddMinutes(6).AddSeconds(300);
        var held = await service.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OnlineLimitsFailure.RateLimited, held.Failure);
        Assert.Equal(sent, handler.Requests.Count);

        clock.Current = Now.AddMinutes(6).AddSeconds(601);
        handler.Answer = _ => Ok(Body(18, 25));
        Assert.True((await service.GetAsync(TestContext.Current.CancellationToken)).IsLive);
        Assert.Equal(sent + 1, handler.Requests.Count);
    }

    /// <summary>
    /// A pause after a 429 survives a restart: a new process sends nothing until it ends, shows the kept reading, and then asks again.
    /// </summary>
    [Fact]
    public async Task Service_PauseSurvivesARestart()
    {
        var first = Service();
        await first.Service.GetAsync(TestContext.Current.CancellationToken);
        first.Clock.Current = Now.AddMinutes(6);
        first.Handler.Answer = _ => TooMany(1800);
        var limited = await first.Service.GetAsync(TestContext.Current.CancellationToken);
        var until = limited.RetryAt!.Value;

        var (service, handler, clock) = Service();
        clock.Current = Now.AddMinutes(10);
        var held = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Requests);
        Assert.Equal(OnlineLimitsFailure.RateLimited, held.Failure);
        Assert.Equal(until, held.RetryAt);
        Assert.Equal(10, held.Limits!.Session!.Percent);

        clock.Current = until.AddSeconds(1);
        Assert.True((await service.GetAsync(TestContext.Current.CancellationToken)).IsLive);
        Assert.Single(handler.Requests);
        Assert.Null(new OnlineLimitsCache(_cachePath).LoadPause(new ClaudeOAuthUsageClient(
            new ClaudeDataLocator(_claudeDir), new HttpClient(), NullLogger<ClaudeOAuthUsageClient>.Instance).GetAccountHash(), until));
    }

    /// <summary>
    /// A pause kept for another account, or one that has ended, is not used.
    /// </summary>
    [Fact]
    public void Cache_Pause_IsPerAccountAndExpires()
    {
        var cache = new OnlineLimitsCache(_cachePath);
        cache.SavePause("acct", Now.AddMinutes(30));

        Assert.Equal(Now.AddMinutes(30), cache.LoadPause("acct", Now));
        Assert.Null(cache.LoadPause("other", Now));
        Assert.Null(cache.LoadPause("acct", Now.AddMinutes(31)));
        Assert.Null(cache.LoadPause("acct", Now.AddMinutes(30) - TimeSpan.FromDays(8)));

        cache.SavePause("acct", Now.AddDays(30));
        Assert.Null(cache.LoadPause("acct", Now));

        cache.SavePause("acct", Now.AddMinutes(30));
        cache.ClearPause();
        Assert.Null(cache.LoadPause("acct", Now));
    }

    /// <summary>
    /// A new process has no reading of its own, so a failure shows the one kept on disk by the earlier run.
    /// </summary>
    [Fact]
    public async Task Service_FailureAtStart_UsesTheReadingKeptOnDisk()
    {
        var first = Service();
        await first.Service.GetAsync(TestContext.Current.CancellationToken);

        var (service, handler, clock) = Service();
        handler.Answer = _ => throw new HttpRequestException("offline");
        clock.Current = Now.AddMinutes(5);

        var result = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OnlineLimitsFailure.Unavailable, result.Failure);
        Assert.True(result.FromCache);
        Assert.Equal(10, result.Limits!.Session!.Percent);
        Assert.Equal(Now, result.ReadAt);
    }

    /// <summary>
    /// The kept reading belongs to an account: after switching accounts it is not shown for the new one.
    /// </summary>
    [Fact]
    public async Task Service_FailureAfterAccountSwitch_DoesNotShowTheOldAccountsReading()
    {
        await Service().Service.GetAsync(TestContext.Current.CancellationToken);
        SignIn("account-b");

        var (service, handler, _) = Service();
        handler.Answer = _ => throw new HttpRequestException("offline");

        var result = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.Null(result.Limits);
        Assert.Equal(OnlineLimitsFailure.Unavailable, result.Failure);
    }

    /// <summary>
    /// A rejected sign-in drops the reading on disk and shows nothing, and no pause is started for it.
    /// </summary>
    [Fact]
    public async Task Service_Rejected_ForgetsTheReading()
    {
        var first = Service();
        await first.Service.GetAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(_cachePath));

        var (service, handler, clock) = Service();
        handler.Answer = _ => new(HttpStatusCode.Unauthorized);
        clock.Current = Now.AddMinutes(5);

        var result = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.Null(result.Limits);
        Assert.Equal(OnlineLimitsFailure.Rejected, result.Failure);
        Assert.False(File.Exists(_cachePath));
        Assert.Null(result.RetryAt);
    }

    /// <summary>
    /// With no sign-in the service reports it and sends nothing.
    /// </summary>
    [Fact]
    public async Task Service_NoSignIn_ReportsItAndSendsNothing()
    {
        File.Delete(Path.Combine(_claudeDir, ".credentials.json"));
        var (service, handler, _) = Service();

        var result = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OnlineLimitsFailure.NotSignedIn, result.Failure);
        Assert.Null(result.Limits);
        Assert.Empty(handler.Requests);
    }

    private string WriteDesktopSamples(params (DateTimeOffset Time, double Session, double Week)[] samples)
    {
        var folder = Path.Combine(_root, "desktop");
        Directory.CreateDirectory(folder);
        var rows = string.Join(",", samples.Select(sample => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{{\"t\":{sample.Time.ToUnixTimeMilliseconds()},\"u\":{{\"fh\":{sample.Session},\"sd\":{sample.Week}}}}}")));
        File.WriteAllText(Path.Combine(folder, ClaudeDesktopUsageReader.FileName), "{\"samples\":[" + rows + "]}");
        return folder;
    }

    /// <summary>
    /// The latest desktop sample is used; a 5-hour sample older than the window and a weekly one older than a week are not.
    /// </summary>
    [Fact]
    public void DesktopReader_UsesTheLatestSampleThatStillApplies()
    {
        var folder = WriteDesktopSamples((Now.AddHours(-9), 80, 40), (Now.AddHours(-2), 35, 45), (Now.AddHours(-30), 90, 10));
        var reader = new ClaudeDesktopUsageReader([folder, Path.Combine(_root, "missing")]);

        var fresh = reader.Read(Now)!.Value;
        Assert.Equal(Now.AddHours(-2).ToUnixTimeMilliseconds(), fresh.ReadAt.ToUnixTimeMilliseconds());
        Assert.Equal(35, fresh.Limits.Session!.Percent);
        Assert.Equal(45, fresh.Limits.Week!.Percent);
        Assert.Null(fresh.Limits.Session.ResetsAt);

        var later = reader.Read(Now.AddHours(4))!.Value;
        Assert.Null(later.Limits.Session);
        Assert.Equal(45, later.Limits.Week!.Percent);

        Assert.Null(reader.Read(Now.AddDays(8)));

        // Seen from three hours earlier only the oldest sample exists yet, and it is 6 hours old: too old for the 5-hour quota.
        var earlier = reader.Read(Now.AddHours(-3))!.Value;
        Assert.Null(earlier.Limits.Session);
        Assert.Equal(40, earlier.Limits.Week!.Percent);
    }

    /// <summary>
    /// A missing, damaged or oddly shaped file is no reading, never an error.
    /// </summary>
    [Theory]
    [InlineData("{ damaged")]
    [InlineData("[]")]
    [InlineData("{\"samples\":{}}")]
    [InlineData("{\"samples\":[{\"t\":\"x\",\"u\":{\"fh\":1,\"sd\":2}},{\"t\":1,\"u\":{\"fh\":-1,\"sd\":2}},{\"t\":1,\"u\":{}}]}")]
    public void DesktopReader_BadFile_IsNoReading(string content)
    {
        var folder = Path.Combine(_root, "desktop");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ClaudeDesktopUsageReader.FileName), content);

        Assert.Null(new ClaudeDesktopUsageReader([folder]).Read(Now));
        Assert.Null(new ClaudeDesktopUsageReader([Path.Combine(_root, "missing")]).Read(Now));
    }

    private (ClaudeOnlineLimitsService Service, Handler Handler, Clock Clock) ServiceWithDesktop(string folder)
    {
        var handler = new Handler(_ => TooMany(1800));
        var clock = new Clock(Now);
        var service = new ClaudeOnlineLimitsService(
            Client(handler), new OnlineLimitsCache(_cachePath), clock, () => 0, new ClaudeDesktopUsageReader([folder]));
        return (service, handler, clock);
    }

    /// <summary>
    /// While the API is limited and nothing else is kept, the desktop sample stands in, marked as such and not live.
    /// </summary>
    [Fact]
    public async Task Service_RateLimited_FallsBackToTheDesktopSample()
    {
        var folder = WriteDesktopSamples((Now.AddHours(-1), 12, 45));
        var (service, _, _) = ServiceWithDesktop(folder);

        var result = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OnlineLimitsFailure.RateLimited, result.Failure);
        Assert.False(result.IsLive);
        Assert.True(result.FromDesktopApp);
        Assert.Equal(12, result.Limits!.Session!.Percent);
        Assert.Equal(45, result.Limits.Week!.Percent);
    }

    /// <summary>
    /// A newer desktop sample wins over an older API reading, and keeps its reset times while they have not passed.
    /// </summary>
    [Fact]
    public async Task Service_NewerDesktopSample_KeepsTheApiResetTimes()
    {
        var folder = WriteDesktopSamples((Now.AddMinutes(30), 50, 60));
        var first = Service();
        await first.Service.GetAsync(TestContext.Current.CancellationToken);

        var (service, handler, clock) = ServiceWithDesktop(folder);
        clock.Current = Now.AddMinutes(45);
        var result = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.True(result.FromDesktopApp);
        Assert.Equal(50, result.Limits!.Session!.Percent);
        Assert.Equal(Now.AddHours(3), result.Limits.Session.ResetsAt);
        Assert.Equal(60, result.Limits.Week!.Percent);
        Assert.Equal(Now.AddDays(4), result.Limits.Week.ResetsAt);
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// An API reading newer than the desktop sample stays as it was.
    /// </summary>
    [Fact]
    public async Task Service_NewerApiReading_BeatsTheDesktopSample()
    {
        var folder = WriteDesktopSamples((Now.AddHours(-1), 1, 1));
        var first = Service();
        await first.Service.GetAsync(TestContext.Current.CancellationToken);

        var (service, _, clock) = ServiceWithDesktop(folder);
        clock.Current = Now.AddMinutes(10);
        var result = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.False(result.FromDesktopApp);
        Assert.Equal(10, result.Limits!.Session!.Percent);
    }

    /// <summary>
    /// With online reading off, only the desktop sample is read: no sign-in, no request.
    /// </summary>
    [Fact]
    public void Service_LocalOnly_ReadsNeitherSignInNorNetwork()
    {
        File.Delete(Path.Combine(_claudeDir, ".credentials.json"));
        var folder = WriteDesktopSamples((Now.AddHours(-1), 12, 45));
        var (service, handler, _) = ServiceWithDesktop(folder);

        var result = service.GetLocalOnly();

        Assert.Equal(OnlineLimitsFailure.Disabled, result.Failure);
        Assert.True(result.FromDesktopApp);
        Assert.Equal(45, result.Limits!.Week!.Percent);
        Assert.Empty(handler.Requests);
        Assert.Same(OnlineLimitsResult.Disabled, ServiceWithDesktop(Path.Combine(_root, "missing")).Service.GetLocalOnly());
    }

    private static string StatuslineInput(DateTimeOffset session, double sessionPercent, DateTimeOffset week, double weekPercent)
        => "{\"model\":{},\"rate_limits\":{\"five_hour\":{\"used_percentage\":"
            + sessionPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"resets_at\":" + session.ToUnixTimeSeconds()
            + "},\"seven_day\":{\"used_percentage\":"
            + weekPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"resets_at\":" + week.ToUnixTimeSeconds() + "}}}";

    /// <summary>
    /// The status line input is parsed to quotas with their reset times; windows that have reset or are malformed are dropped.
    /// </summary>
    [Fact]
    public void StatuslineStore_ParsesTheQuotasOfTheInput()
    {
        var limits = StatuslineLimitsStore.Parse(StatuslineInput(Now.AddHours(2), 23.5, Now.AddDays(3), 41), Now)!;
        Assert.Equal(23.5, limits.Session!.Percent);
        Assert.Equal(Now.AddHours(2).ToUnixTimeSeconds(), limits.Session.ResetsAt!.Value.ToUnixTimeSeconds());
        Assert.Equal(41, limits.Week!.Percent);

        var reset = StatuslineLimitsStore.Parse(StatuslineInput(Now.AddHours(-1), 90, Now.AddDays(3), 41), Now)!;
        Assert.Null(reset.Session);
        Assert.Equal(41, reset.Week!.Percent);

        Assert.Null(StatuslineLimitsStore.Parse("{ damaged", Now));
        Assert.Null(StatuslineLimitsStore.Parse("{}", Now));
        Assert.Null(StatuslineLimitsStore.Parse("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":-1},\"seven_day\":{\"used_percentage\":\"x\"}}}", Now));
        Assert.Null(StatuslineLimitsStore.Parse("[]", Now));
    }

    /// <summary>
    /// A recorded input is read back while its windows have not reset; input without quotas keeps what was there.
    /// </summary>
    [Fact]
    public void StatuslineStore_RecordsAndReadsBack()
    {
        var store = new StatuslineLimitsStore(Path.Combine(_root, "statusline", "limits.json"));
        Assert.Null(store.Read(Now));

        store.Record(StatuslineInput(Now.AddHours(2), 20, Now.AddDays(3), 40), Now);
        store.Record("{\"model\":{}}", Now.AddMinutes(1));

        var read = store.Read(Now.AddMinutes(5))!.Value;
        Assert.Equal(20, read.Limits.Session!.Percent);
        Assert.Equal(Now.ToUnixTimeSeconds(), read.ReadAt.ToUnixTimeSeconds());

        var later = store.Read(Now.AddHours(3))!.Value;
        Assert.Null(later.Limits.Session);
        Assert.Equal(40, later.Limits.Week!.Percent);
        Assert.Null(store.Read(Now.AddDays(8)));
    }

    /// <summary>
    /// A recent status line reading is live and needs no request; an older one is a sample that carries its reset times.
    /// </summary>
    [Fact]
    public async Task Service_StatuslineReading_IsLiveWhenRecentAndNeedsNoRequest()
    {
        var store = new StatuslineLimitsStore(Path.Combine(_root, "statusline.json"));
        store.Record(StatuslineInput(Now.AddHours(2), 20, Now.AddDays(3), 40), Now);
        var handler = new Handler(_ => TooMany(1800));
        var clock = new Clock(Now.AddMinutes(2));
        var service = new ClaudeOnlineLimitsService(
            Client(handler), new OnlineLimitsCache(_cachePath), clock, () => 0, null, store);

        var fresh = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.True(fresh.IsLive);
        Assert.True(fresh.FromStatusline);
        Assert.Equal(20, fresh.Limits!.Session!.Percent);
        Assert.Equal(Now.AddHours(2).ToUnixTimeSeconds(), fresh.Limits.Session.ResetsAt!.Value.ToUnixTimeSeconds());
        Assert.Empty(handler.Requests);

        clock.Current = Now.AddMinutes(30);
        var stale = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.False(stale.IsLive);
        Assert.True(stale.FromStatusline);
        Assert.Equal(OnlineLimitsFailure.RateLimited, stale.Failure);
        Assert.Equal(20, stale.Limits!.Session!.Percent);
        Assert.Single(handler.Requests);

        clock.Current = Now.AddMinutes(45);
        Assert.True(service.GetLocalOnly().FromStatusline);
    }

    /// <summary>
    /// A data service built without the online service reports it as disabled instead of touching anything.
    /// </summary>
    [Fact]
    public void Disabled_IsNotLive()
    {
        Assert.False(OnlineLimitsResult.Disabled.IsLive);
        Assert.Equal(OnlineLimitsFailure.Disabled, OnlineLimitsResult.Disabled.Failure);
    }
}
