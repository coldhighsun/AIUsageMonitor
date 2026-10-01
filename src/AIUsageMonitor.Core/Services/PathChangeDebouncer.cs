namespace AIUsageMonitor.Core.Services;

/// <summary>
/// Collects file paths reported by a burst of file-system events and hands them over once, in a single batch, after
/// the burst has settled. A transcript that is being written to raises an event for almost every append; without
/// this the same file would be refreshed over and over. A path reported many times within the burst appears once in
/// the batch. To keep a never-ending stream of events from postponing the refresh forever, the batch is delivered at
/// the latest <c>maxWait</c> after its first path arrived.
/// </summary>
internal sealed class PathChangeDebouncer : IDisposable
{
    /// <summary>
    /// How long without a new event before the batch is delivered.
    /// </summary>
    private readonly TimeSpan _delay;

    /// <summary>
    /// The longest a batch may be held back, counted from its first path.
    /// </summary>
    private readonly TimeSpan _maxWait;

    /// <summary>
    /// Receives each batch of distinct paths; runs on a thread-pool thread, one batch at a time.
    /// </summary>
    private readonly Action<IReadOnlyList<string>> _flush;

    /// <summary>
    /// The clock used to measure how long the current batch has been waiting.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Guards <see cref="_pending"/>, <see cref="_firstPendingTimestamp"/> and <see cref="_disposed"/>.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// Serializes deliveries so two batches are never processed concurrently.
    /// </summary>
    private readonly Lock _flushLock = new();

    /// <summary>
    /// The paths collected since the last delivery.
    /// </summary>
    private readonly HashSet<string> _pending;

    /// <summary>
    /// The timer that triggers delivery.
    /// </summary>
    private readonly ITimer _timer;

    /// <summary>
    /// The timestamp at which the first path of the current batch arrived.
    /// </summary>
    private long _firstPendingTimestamp;

    /// <summary>
    /// Whether <see cref="Dispose"/> has been called.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PathChangeDebouncer"/> class.
    /// </summary>
    /// <param name="delay">How long without a new event before the batch is delivered.</param>
    /// <param name="maxWait">The longest a batch may be held back, counted from its first path.</param>
    /// <param name="flush">
    /// Receives each batch of distinct paths. It must handle its own failures: an exception escaping it
    /// would terminate the process from the timer thread.
    /// </param>
    /// <param name="timeProvider">The clock and timer factory, so delivery can be controlled in tests.</param>
    /// <param name="comparer">The comparer that decides whether two paths are the same file.</param>
    public PathChangeDebouncer(
        TimeSpan delay,
        TimeSpan maxWait,
        Action<IReadOnlyList<string>> flush,
        TimeProvider timeProvider,
        IEqualityComparer<string>? comparer = null)
    {
        _delay = delay;
        _maxWait = maxWait;
        _flush = flush;
        _timeProvider = timeProvider;
        _pending = new HashSet<string>(comparer ?? StringComparer.Ordinal);
        _timer = timeProvider.CreateTimer(_ => Deliver(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Records a changed path and (re)schedules delivery of the batch.
    /// </summary>
    /// <param name="path">The full path of the changed file.</param>
    public void Add(string path)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_pending.Count == 0)
            {
                _firstPendingTimestamp = _timeProvider.GetTimestamp();
            }

            _pending.Add(path);

            var due = _maxWait - _timeProvider.GetElapsedTime(_firstPendingTimestamp);
            if (due > _delay)
            {
                due = _delay;
            }

            _timer.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Stops the timer; paths that have not been delivered yet are dropped.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _pending.Clear();
        }

        _timer.Dispose();
    }

    /// <summary>
    /// Takes the collected paths and passes them to the flush callback.
    /// </summary>
    private void Deliver()
    {
        string[] batch;
        lock (_lock)
        {
            if (_disposed || _pending.Count == 0)
            {
                return;
            }

            batch = [.. _pending];
            _pending.Clear();
        }

        lock (_flushLock)
        {
            _flush(batch);
        }
    }
}
