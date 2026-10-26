namespace NetworkTrafficController.Core.Monitoring;

/// <summary>
/// 速度の時系列（bytes/s）を上限付きリングバッファで保持する（ADR-0005 §7、既定 300 点 = 5 分@1s）。
/// UI スレッドからのみ使う。全体用と、選択プロセス用に個別のインスタンスを持たせる。
/// </summary>
public sealed class SpeedHistory
{
    private readonly double[] _out;
    private readonly double[] _in;
    private int _count;
    private int _head;

    /// <summary>保持する点数を指定して生成する。</summary>
    public SpeedHistory(int capacity = 300)
    {
        Capacity = capacity;
        _out = new double[capacity];
        _in = new double[capacity];
    }

    /// <summary>保持できる点数。</summary>
    public int Capacity { get; }

    /// <summary>現在の点数。</summary>
    public int Count => _count;

    /// <summary>1 点追加する。</summary>
    public void Add(double outBytesPerSecond, double inBytesPerSecond)
    {
        _out[_head] = outBytesPerSecond;
        _in[_head] = inBytesPerSecond;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity)
        {
            _count++;
        }
    }

    /// <summary>古い順に上り速度を書き出す。</summary>
    public void CopyOut(Span<double> destination) => Copy(_out, destination);

    /// <summary>古い順に下り速度を書き出す。</summary>
    public void CopyIn(Span<double> destination) => Copy(_in, destination);

    /// <summary>
    /// 複数の履歴を、最新の点をそろえて足し合わせ、古い順に書き出す。点の少ない履歴（後から現れたプロセス）は、
    /// それより古い時点を 0 とみなす。複数のプロセスを選んだときのグラフに使う。
    /// </summary>
    /// <returns>書き出した点数（最も長い履歴の点数と、書き出し先の長さの小さい方）。</returns>
    public static int SumLatest(IEnumerable<SpeedHistory> histories, Span<double> outSum, Span<double> inSum)
    {
        var list = histories as IReadOnlyCollection<SpeedHistory> ?? histories.ToList();
        int n = Math.Min(Math.Min(outSum.Length, inSum.Length), list.Count == 0 ? 0 : list.Max(h => h.Count));
        outSum[..n].Clear();
        inSum[..n].Clear();
        foreach (var h in list)
        {
            int k = Math.Min(h.Count, n);
            if (k == 0)
            {
                continue;
            }

            // 一時コピーを作らず、リングバッファから最新の k 点を直接読む
            int start = (h._head - h._count + h.Capacity) % h.Capacity;
            for (int j = 0; j < k; j++)
            {
                int src = (start + h._count - k + j) % h.Capacity;
                outSum[n - k + j] += h._out[src];
                inSum[n - k + j] += h._in[src];
            }
        }

        return n;
    }

    /// <summary>全消去する。</summary>
    public void Clear()
    {
        _count = 0;
        _head = 0;
    }

    private void Copy(double[] source, Span<double> destination)
    {
        int n = Math.Min(_count, destination.Length);
        int start = (_head - _count + Capacity) % Capacity;
        for (int i = 0; i < n; i++)
        {
            destination[i] = source[(start + i) % Capacity];
        }
    }
}
