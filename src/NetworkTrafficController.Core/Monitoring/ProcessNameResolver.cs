using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NetworkTrafficController.Core.Native;

namespace NetworkTrafficController.Core.Monitoring;

/// <summary>PID から解決したプロセス情報。</summary>
/// <param name="Pid">プロセス ID。</param>
/// <param name="Name">表示名（exe のファイル名。取れない場合は固定名か「不明」）。</param>
/// <param name="ImagePath">フルパス（取れない場合は null）。</param>
/// <param name="CreationTime">プロセス開始時刻（FILETIME、取れない場合は 0）。PID 再利用の判別に使う。</param>
/// <param name="Resolved">名前を取得できたか。</param>
public sealed record ProcessInfo(uint Pid, string Name, string? ImagePath, long CreationTime, bool Resolved);

/// <summary>
/// PID → exe 名の解決（ADR-0004 §6）。<c>OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)</c> + <c>QueryFullProcessImageNameW</c>。
/// 結果は TTL 付きでキャッシュし、PID が再利用されても古い名前を出し続けないようにする。
/// パケット処理スレッドからは呼ばない（表示側で呼ぶ）。
/// </summary>
public sealed unsafe class ProcessNameResolver
{
    private const int MaxPathChars = 32 * 1024;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<uint, (ProcessInfo Info, long ResolvedAt)> _cache = new();
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;

    /// <summary>ロガーと時間源を指定して生成する。</summary>
    public ProcessNameResolver(ILogger logger, TimeProvider time, TimeSpan? ttl = null)
    {
        _logger = logger;
        _time = time;
        _ttl = ttl ?? DefaultTtl;
    }

    /// <summary>PID を解決する（TTL 内はキャッシュ）。</summary>
    public ProcessInfo Resolve(uint pid)
    {
        long now = _time.GetTimestamp();
        if (_cache.TryGetValue(pid, out var cached) && _time.GetElapsedTime(cached.ResolvedAt, now) < _ttl)
        {
            return cached.Info;
        }

        var info = Query(pid);
        _cache[pid] = (info, now);
        return info;
    }

    /// <summary>キャッシュを捨てる。</summary>
    public void Invalidate(uint pid) => _cache.TryRemove(pid, out _);

    private ProcessInfo Query(uint pid)
    {
        switch (pid)
        {
            case 0:
                return new ProcessInfo(pid, "System Idle Process", null, 0, true);
            case 4:
                return new ProcessInfo(pid, "System", null, 0, true);
        }

        using var handle = ProcessNative.OpenProcess(ProcessNative.ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            _logger.LogDebug("OpenProcess({Pid}) failed: Win32 error {Error}", pid, error);
            return new ProcessInfo(pid, $"<unknown: PID {pid}>", null, 0, false);
        }

        long creation = 0, exit, kernel, user;
        if (!ProcessNative.GetProcessTimes(handle, &creation, &exit, &kernel, &user))
        {
            creation = 0;
        }

        var buffer = System.Buffers.ArrayPool<char>.Shared.Rent(MaxPathChars);
        try
        {
            uint size = MaxPathChars;
            bool ok;
            fixed (char* p = buffer)
            {
                ok = ProcessNative.QueryFullProcessImageName(handle, 0, p, &size);
            }

            if (!ok)
            {
                int error = Marshal.GetLastPInvokeError();
                _logger.LogDebug("QueryFullProcessImageName({Pid}) failed: Win32 error {Error}", pid, error);
                return new ProcessInfo(pid, $"<unknown: PID {pid}>", null, creation, false);
            }

            string path = new(buffer, 0, (int)Math.Min(size, MaxPathChars));
            return new ProcessInfo(pid, Path.GetFileName(path), path, creation, true);
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(buffer);
        }
    }
}
