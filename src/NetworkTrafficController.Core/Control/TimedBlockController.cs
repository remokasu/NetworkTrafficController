using Microsoft.Extensions.Logging;

namespace NetworkTrafficController.Core.Control;

/// <summary>
/// 「N 秒だけ Block して自動復帰」する時限 Block（SPEC）。Block の適用・解除は注入されたコールバックで行うので、
/// 全対象への上乗せ（<see cref="ControlPolicyStore.SetTimedBlock"/>）にも、単一対象への適用にも使える。
/// <see cref="TimeProvider"/> のタイマで復帰する。時限中に手動で OFF（<see cref="Cancel"/>）されたら即復帰し、
/// タイマは破棄する（SPEC 決定事項）。
/// </summary>
public sealed class TimedBlockController : IDisposable
{
    private readonly Action<bool> _setBlock;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private ITimer? _timer;

    /// <summary>Block の適用/解除コールバック・時間源・ロガーを指定して生成する。</summary>
    public TimedBlockController(Action<bool> setBlock, TimeProvider time, ILogger logger)
    {
        _setBlock = setBlock;
        _time = time;
        _logger = logger;
    }

    /// <summary>時限 Block が進行中か。</summary>
    public bool IsActive
    {
        get { lock (_lock) { return _timer is not null; } }
    }

    /// <summary>N 秒だけ Block する。既に進行中なら、タイマを引き直す（Block は上乗せしたまま）。</summary>
    public void Start(TimeSpan duration)
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _setBlock(true);
            _timer = _time.CreateTimer(_ => Restore(), null, duration, Timeout.InfiniteTimeSpan);
            _logger.LogInformation("Timed block started for {Seconds:F1}s", duration.TotalSeconds);
        }
    }

    /// <summary>時限 Block を取り消して即復帰する（手動 OFF）。</summary>
    public void Cancel() => Restore();

    private void Restore()
    {
        lock (_lock)
        {
            if (_timer is null)
            {
                return;
            }

            _timer.Dispose();
            _timer = null;
            _setBlock(false);
            _logger.LogInformation("Timed block restored");
        }
    }

    /// <inheritdoc />
    public void Dispose() => Restore();
}
