using System.Collections.Concurrent;
using System.Net;
using GW2AccountMCP.Gw2;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class Gw2ApiBudgetTests
{
    private static readonly TimeSpan MinimumStartInterval = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task StartGate_spaces_concurrent_starts_to_four_per_second()
    {
        var timeProvider = new ControllableTimeProvider();
        var gate = new Gw2ApiStartGate(timeProvider);
        var starts = new ConcurrentQueue<DateTimeOffset>();

        var attempts = Enumerable.Range(0, 5).Select(_ => RecordStartAsync(gate, timeProvider, starts)).ToArray();
        await Task.Yield();

        Assert.Single(starts);
        for (var expectedStarts = 2; expectedStarts <= 5; expectedStarts++)
        {
            var pendingAttempt = attempts.First(attempt => !attempt.IsCompleted);
            timeProvider.Advance(MinimumStartInterval);
            await pendingAttempt;
            Assert.Equal(expectedStarts, starts.Count);
        }

        await Task.WhenAll(attempts);
        var orderedStarts = starts.Order().ToArray();
        Assert.Equal(5, orderedStarts.Length);
        Assert.All(orderedStarts.Zip(orderedStarts.Skip(1)), pair =>
            Assert.True(pair.Second - pair.First >= MinimumStartInterval));
        Assert.Equal(TimeSpan.FromSeconds(1), orderedStarts[^1] - orderedStarts[0]);
    }

    [Fact]
    public async Task StartGate_cancellation_while_queued_does_not_consume_a_start_slot()
    {
        var timeProvider = new ControllableTimeProvider();
        var gate = new Gw2ApiStartGate(timeProvider);

        await gate.WaitAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelledAttempt = gate.WaitAsync(cancellation.Token);
        var subsequentAttempt = gate.WaitAsync(CancellationToken.None);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledAttempt);

        timeProvider.Advance(MinimumStartInterval);
        await subsequentAttempt;

        Assert.Equal(timeProvider.GetUtcNow(), DateTimeOffset.UnixEpoch + MinimumStartInterval);
    }

    [Fact]
    public async Task Handler_applies_the_gate_to_each_attempt_before_sending()
    {
        var timeProvider = new ControllableTimeProvider();
        var gate = new Gw2ApiStartGate(timeProvider);
        var recordingHandler = new RecordingHandler(timeProvider);
        using var client = new HttpClient(new Gw2ApiBudgetHandler(gate) { InnerHandler = recordingHandler })
        {
            BaseAddress = new Uri("https://example.test")
        };

        using var firstResponse = await client.GetAsync("/first-attempt");
        var secondAttempt = client.GetAsync("/retry-attempt");
        await Task.Yield();
        Assert.Single(recordingHandler.StartTimes);

        timeProvider.Advance(MinimumStartInterval);
        using var secondResponse = await secondAttempt;

        var starts = recordingHandler.StartTimes.ToArray();
        Assert.Equal(2, starts.Length);
        Assert.True(starts[1] - starts[0] >= MinimumStartInterval);
    }

    [Fact]
    public void Lease_creates_parent_exclusively_and_leaves_the_lock_file_after_release()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GW2AccountMCP.Tests", Guid.NewGuid().ToString("N"));
        var lockPath = Path.Combine(directory, "leases", "gw2-api-budget.lock");
        var options = new Gw2ApiBudgetLeaseOptions(lockPath);

        using (var firstLease = Gw2ApiBudgetLease.Acquire(options))
        {
            Assert.True(Directory.Exists(Path.GetDirectoryName(lockPath)!));
            Assert.True(File.Exists(lockPath));
            var exception = Assert.Throws<InvalidOperationException>(() => Gw2ApiBudgetLease.Acquire(options));
            Assert.Contains("GW2 API budget lease", exception.Message, StringComparison.Ordinal);
        }

        using var reacquiredLease = Gw2ApiBudgetLease.Acquire(options);
        Assert.True(File.Exists(lockPath));
    }

    private static async Task RecordStartAsync(
        Gw2ApiStartGate gate,
        TimeProvider timeProvider,
        ConcurrentQueue<DateTimeOffset> starts)
    {
        await gate.WaitAsync(CancellationToken.None);
        starts.Enqueue(timeProvider.GetUtcNow());
    }

    private sealed class RecordingHandler(TimeProvider timeProvider) : HttpMessageHandler
    {
        public ConcurrentQueue<DateTimeOffset> StartTimes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            StartTimes.Enqueue(timeProvider.GetUtcNow());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class ControllableTimeProvider : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<ControllableTimer> timers = [];
        private DateTimeOffset utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow()
        {
            lock (sync)
            {
                return utcNow;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ControllableTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            List<ControllableTimer> dueTimers;
            lock (sync)
            {
                utcNow += elapsed;
                dueTimers = timers.Where(timer => timer.IsDue(utcNow)).ToList();
            }

            foreach (var timer in dueTimers)
            {
                timer.Fire();
            }
        }

        private void Change(ControllableTimer timer, TimeSpan dueTime)
        {
            lock (sync)
            {
                if (!timers.Contains(timer))
                {
                    timers.Add(timer);
                }

                timer.SetDueTime(dueTime == Timeout.InfiniteTimeSpan ? null : utcNow + dueTime);
            }
        }

        private void Dispose(ControllableTimer timer)
        {
            lock (sync)
            {
                timers.Remove(timer);
            }
        }

        private sealed class ControllableTimer(
            ControllableTimeProvider timeProvider,
            TimerCallback callback,
            object? state) : ITimer
        {
            private DateTimeOffset? dueTime;
            private bool disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed)
                {
                    return false;
                }

                timeProvider.Change(this, dueTime);
                return true;
            }

            public bool IsDue(DateTimeOffset now) => !disposed && dueTime is { } due && due <= now;

            public void Fire()
            {
                if (disposed || dueTime is null)
                {
                    return;
                }

                dueTime = null;
                callback(state);
            }

            public void SetDueTime(DateTimeOffset? value) => dueTime = value;

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                timeProvider.Dispose(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
