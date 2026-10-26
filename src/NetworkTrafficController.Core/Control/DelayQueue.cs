namespace NetworkTrafficController.Core.Control;

/// <summary>
/// 解放時刻（<see cref="TimeProvider.GetTimestamp"/> の値）で並ぶスレッド安全な遅延キュー。
/// シェーパスレッドが <see cref="TryDequeueDue"/> で期限の来た要素を取り出す。
/// </summary>
internal sealed class DelayQueue<T>
{
    private readonly PriorityQueue<T, long> _heap = new();
    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private bool _closed;

    /// <summary>時間源を指定して生成する。</summary>
    public DelayQueue(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>キュー内の要素数。</summary>
    public int Count
    {
        get { lock (_lock) { return _heap.Count; } }
    }

    /// <summary>要素を追加し、待機中のシェーパを起こす。閉じた後は追加せず false を返す（呼び出し側が即処理する）。</summary>
    public bool TryEnqueue(T item, long releaseTimestamp)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return false;
            }

            _heap.Enqueue(item, releaseTimestamp);
            Monitor.Pulse(_lock);
            return true;
        }
    }

    /// <summary>
    /// 期限の来た要素を 1 つ取り出す。無ければ次の期限（最大 <paramref name="maxWait"/>）まで待つ。
    /// <see cref="CloseAndDrain"/> 後は false を返す。
    /// </summary>
    public bool TryDequeueDue(TimeSpan maxWait, out T item)
    {
        lock (_lock)
        {
            while (!_closed)
            {
                if (_heap.TryPeek(out var head, out long release))
                {
                    long now = _time.GetTimestamp();
                    if (release <= now)
                    {
                        _heap.Dequeue();
                        item = head;
                        return true;
                    }

                    TimeSpan remaining = _time.GetElapsedTime(now, release);
                    Monitor.Wait(_lock, remaining < maxWait ? remaining : maxWait);
                    continue;
                }

                Monitor.Wait(_lock, maxWait);
                if (_heap.Count == 0)
                {
                    item = default!;
                    return false; // タイムアウト（呼び出し側でループ継続判定）
                }
            }

            item = default!;
            return false;
        }
    }

    /// <summary>待機を解除し、以後の取り出しを止める。残りの要素を期限に関係なく全部返す。</summary>
    public List<T> CloseAndDrain()
    {
        lock (_lock)
        {
            _closed = true;
            var rest = new List<T>(_heap.Count);
            while (_heap.TryDequeue(out var item, out _))
            {
                rest.Add(item);
            }

            Monitor.PulseAll(_lock);
            return rest;
        }
    }
}
