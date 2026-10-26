namespace NetworkTrafficController.Core.Control;

/// <summary>
/// トークンバケット（バイト単位）。ADR-0005 のシェーピング方式。
/// パケット到着時に <see cref="Reserve"/> でトークンを消費（不足分は負に振り込む）し、
/// 不足していれば「あと何秒で足りるか」を返す。これにより上限超過分を即破棄せず遅延させ、
/// 平均レートを指定値に保つ。単調時刻は <see cref="TimeProvider.GetTimestamp"/> を使う。
/// スレッド安全ではない（呼び出し側が (対象,方向) ごとにロックする）。
/// </summary>
public sealed class TokenBucket
{
    private readonly TimeProvider _time;
    private double _ratePerSecond;
    private double _capacity;
    private double _tokens;
    private long _lastRefill;

    /// <summary>レート（バイト/秒）と容量（バースト、バイト）と時間源を指定して生成する。</summary>
    public TokenBucket(double ratePerSecond, double capacity, TimeProvider time)
    {
        _time = time;
        _lastRefill = time.GetTimestamp();
        Reconfigure(ratePerSecond, capacity);
        _tokens = _capacity;
    }

    /// <summary>現在のレート（バイト/秒）。</summary>
    public double RatePerSecond => _ratePerSecond;

    /// <summary>現在のトークン残高（テスト・観測用。負になりうる）。</summary>
    public double Tokens => _tokens;

    /// <summary>レートと容量を変更する。容量は 1 パケット最大長を下回らないよう呼び出し側で決める。</summary>
    public void Reconfigure(double ratePerSecond, double capacity)
    {
        _ratePerSecond = Math.Max(0, ratePerSecond);
        _capacity = Math.Max(1, capacity);
        if (_tokens > _capacity)
        {
            _tokens = _capacity;
        }
    }

    /// <summary>
    /// <paramref name="bytes"/> バイト分を予約する。トークンを補充してから消費し（不足分は負残高）、
    /// 不足していた場合はそれが解消されるまでの待ち時間を返す。
    /// </summary>
    /// <returns>いま通してよいなら <see cref="TimeSpan.Zero"/>、遅らせるべきなら待ち時間。</returns>
    /// <exception cref="InvalidOperationException">レートが 0 以下（停止は呼び出し側で予約前に破棄として扱うこと）。</exception>
    public TimeSpan Reserve(int bytes)
    {
        if (_ratePerSecond <= 0)
        {
            throw new InvalidOperationException("Cannot reserve on a bucket with rate 0 (stopped). Treat stopped as dropped before reserving");
        }

        Refill();
        _tokens -= bytes;
        if (_tokens >= 0)
        {
            return TimeSpan.Zero;
        }

        // 念のため待ち時間に上限を設ける（レート下限とキュー上限で通常は数秒以内。桁あふれで即時解放にならないように）
        double seconds = Math.Min(-_tokens / _ratePerSecond, MaxWait.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>1 パケットの待ち時間の上限。</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1);

    private void Refill()
    {
        long now = _time.GetTimestamp();
        double elapsed = _time.GetElapsedTime(_lastRefill, now).TotalSeconds;
        _lastRefill = now;
        if (elapsed <= 0)
        {
            return;
        }

        _tokens = Math.Min(_capacity, _tokens + (elapsed * _ratePerSecond));
    }
}
