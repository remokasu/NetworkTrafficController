namespace NetworkTrafficController.Core.Monitoring;

/// <summary>プロセス 1 件の速度（bytes/s）と累計。</summary>
public sealed record ProcessRate(uint Pid, double OutBytesPerSecond, double InBytesPerSecond, TrafficCounters Total, int ConnectionCount);

/// <summary>2 つのスナップショットの差分から速度を求める。GUI / CLI 共通。</summary>
public sealed class RateCalculator
{
    private readonly TimeProvider _time;
    private MonitorSnapshot? _previous;

    /// <summary>時間源を指定して生成する。</summary>
    public RateCalculator(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>新しいスナップショットを与え、前回との差分から速度を計算する。初回は速度 0。</summary>
    public (double TotalOutBps, double TotalInBps, IReadOnlyList<ProcessRate> Processes) Update(MonitorSnapshot current)
    {
        var previous = _previous;
        _previous = current;
        if (previous is null)
        {
            return (0, 0, current.Processes.Select(p => new ProcessRate(p.Pid, 0, 0, p.Counters, p.ConnectionCount)).ToList());
        }

        double seconds = _time.GetElapsedTime(previous.Timestamp, current.Timestamp).TotalSeconds;
        if (seconds <= 0)
        {
            seconds = double.Epsilon;
        }

        var previousByPid = previous.Processes.ToDictionary(p => p.Pid);
        var rates = new List<ProcessRate>(current.Processes.Count);
        foreach (var p in current.Processes)
        {
            var delta = previousByPid.TryGetValue(p.Pid, out var prev) ? p.Counters.Subtract(prev.Counters) : p.Counters;
            rates.Add(new ProcessRate(p.Pid, delta.OutBytes / seconds, delta.InBytes / seconds, p.Counters, p.ConnectionCount));
        }

        var totalDelta = current.Total.Subtract(previous.Total);
        return (totalDelta.OutBytes / seconds, totalDelta.InBytes / seconds, rates);
    }
}
