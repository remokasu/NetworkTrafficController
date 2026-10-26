namespace NetworkTrafficController.Core.Tests;

/// <summary>テスト用の手動で進める時間源（マイクロ秒分解能）。<see cref="Advance"/> で時計を進めるとタイマも発火する。</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<FakeTimer> _timers = new();
    private long _ticks;
    private DateTimeOffset _utcNow = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => 1_000_000;

    public override long GetTimestamp()
    {
        lock (_lock) { return _ticks; }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock) { return _utcNow; }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state, dueTime, period);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        List<FakeTimer> due;
        lock (_lock)
        {
            _ticks += (long)(delta.TotalSeconds * TimestampFrequency);
            _utcNow += delta;
            due = _timers.Where(t => t.IsDue(_ticks)).ToList();
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private void Remove(FakeTimer timer)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly FakeTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private long _dueTicks;
        private long _periodTicks;
        private bool _disposed;

        public FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            Schedule(dueTime, period);
        }

        public bool IsDue(long now) => !_disposed && _dueTicks >= 0 && now >= _dueTicks;

        public void Fire()
        {
            if (_disposed)
            {
                return;
            }

            if (_periodTicks > 0)
            {
                _dueTicks += _periodTicks;
            }
            else
            {
                _dueTicks = -1; // 一度きり
            }

            _callback(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
            {
                return false;
            }

            Schedule(dueTime, period);
            return true;
        }

        private void Schedule(TimeSpan dueTime, TimeSpan period)
        {
            _dueTicks = dueTime == Timeout.InfiniteTimeSpan ? -1 : _owner.GetTimestamp() + (long)(dueTime.TotalSeconds * _owner.TimestampFrequency);
            _periodTicks = period == Timeout.InfiniteTimeSpan ? 0 : (long)(period.TotalSeconds * _owner.TimestampFrequency);
        }

        public void Dispose()
        {
            _disposed = true;
            _owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
