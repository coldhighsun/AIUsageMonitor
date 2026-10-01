using AIUsageMonitor.Core.Services;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Services;

/// <summary>
/// Tests for <see cref="PathChangeDebouncer"/>, using real (short) timers.
/// </summary>
public sealed class PathChangeDebouncerTests : IDisposable
{
    /// <summary>
    /// The quiet period used by the tests.
    /// </summary>
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The maximum hold-back used by the tests.
    /// </summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// A generous upper bound for waiting on something that should happen.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The batches delivered so far, each with the time it arrived.
    /// </summary>
    private readonly List<(DateTimeOffset At, string[] Paths)> _batches = [];

    /// <summary>
    /// Signalled whenever a batch is delivered.
    /// </summary>
    private readonly SemaphoreSlim _delivered = new(0);

    /// <summary>
    /// The debouncer under test.
    /// </summary>
    private readonly PathChangeDebouncer _sut;

    /// <summary>
    /// Creates the debouncer, recording every batch it delivers.
    /// </summary>
    public PathChangeDebouncerTests()
    {
        _sut = new(Delay, MaxWait, Record, TimeProvider.System);
    }

    /// <summary>
    /// Disposes the debouncer and the semaphore.
    /// </summary>
    public void Dispose()
    {
        _sut.Dispose();
        _delivered.Dispose();
    }

    /// <summary>
    /// Records a delivered batch.
    /// </summary>
    /// <param name="paths">The delivered paths.</param>
    private void Record(IReadOnlyList<string> paths)
    {
        lock (_batches)
        {
            _batches.Add((DateTimeOffset.UtcNow, [.. paths]));
        }

        _delivered.Release();
    }

    /// <summary>
    /// Verifies that a burst of events for one path produces a single delivery containing the path once.
    /// </summary>
    [Fact]
    public async Task Add_BurstForSamePath_DeliversOneBatchWithThePathOnce()
    {
        for (var i = 0; i < 10; i++)
        {
            _sut.Add("a");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(await _delivered.WaitAsync(Patience, TestContext.Current.CancellationToken));
        await Task.Delay(Delay * 3, TestContext.Current.CancellationToken);

        lock (_batches)
        {
            Assert.Single(_batches);
            Assert.Equal(["a"], _batches[0].Paths);
        }
    }

    /// <summary>
    /// Verifies that different paths in one burst are delivered together.
    /// </summary>
    [Fact]
    public async Task Add_DifferentPathsInOneBurst_AreDeliveredTogether()
    {
        _sut.Add("a");
        _sut.Add("b");
        _sut.Add("a");

        Assert.True(await _delivered.WaitAsync(Patience, TestContext.Current.CancellationToken));

        lock (_batches)
        {
            Assert.Equal(["a", "b"], _batches.Single().Paths.Order());
        }
    }

    /// <summary>
    /// Verifies that nothing is delivered before the quiet period has elapsed.
    /// </summary>
    [Fact]
    public async Task Add_DoesNotDeliverBeforeQuietPeriod()
    {
        _sut.Add("a");

        Assert.False(await _delivered.WaitAsync(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken));
        Assert.True(await _delivered.WaitAsync(Patience, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that a constant stream of events cannot postpone delivery beyond the maximum wait.
    /// </summary>
    [Fact]
    public async Task Add_ContinuousEvents_DeliversWithinMaxWait()
    {
        var started = DateTimeOffset.UtcNow;
        var stop = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);

        while (DateTimeOffset.UtcNow < stop && _delivered.CurrentCount == 0)
        {
            _sut.Add("busy");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.True(_delivered.CurrentCount > 0, "no delivery while events kept arriving");
        lock (_batches)
        {
            Assert.True(_batches[0].At - started < MaxWait + TimeSpan.FromSeconds(1.5));
        }
    }

    /// <summary>
    /// Verifies that events after a delivery start a new batch.
    /// </summary>
    [Fact]
    public async Task Add_AfterDelivery_StartsNewBatch()
    {
        _sut.Add("a");
        Assert.True(await _delivered.WaitAsync(Patience, TestContext.Current.CancellationToken));

        _sut.Add("b");
        Assert.True(await _delivered.WaitAsync(Patience, TestContext.Current.CancellationToken));

        lock (_batches)
        {
            Assert.Equal([["a"], ["b"]], _batches.Select(b => b.Paths));
        }
    }

    /// <summary>
    /// Verifies that disposing drops pending paths and later events.
    /// </summary>
    [Fact]
    public async Task Dispose_DropsPendingAndIgnoresLaterAdds()
    {
        _sut.Add("a");
        _sut.Dispose();
        _sut.Add("b");

        Assert.False(await _delivered.WaitAsync(Delay * 4, TestContext.Current.CancellationToken));
    }
}
