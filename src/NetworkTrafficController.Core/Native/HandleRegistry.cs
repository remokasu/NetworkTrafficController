using System.Collections.Concurrent;

namespace NetworkTrafficController.Core.Native;

/// <summary>
/// 開いている WinDivert ハンドルの台帳。プロセス終了・未処理例外時に <see cref="CloseAll"/> で
/// 全ハンドルを閉じ、通信が止まったままにならないようにする（フェイルセーフ、CLAUDE.md 設計上の要点 5）。
/// </summary>
public static class HandleRegistry
{
    private static readonly ConcurrentDictionary<object, byte> Handles = new();
    private static int _hooked;

    /// <summary>現在開いているハンドル数。</summary>
    public static int OpenCount => Handles.Count;

    /// <summary>
    /// <see cref="AppDomain.ProcessExit"/> と <see cref="AppDomain.UnhandledException"/> に
    /// <see cref="CloseAll"/> を登録する（1 回だけ）。GUI 側の DispatcherUnhandledException は呼び出し側で追加登録する。
    /// </summary>
    public static void HookProcessLifetime()
    {
        if (Interlocked.Exchange(ref _hooked, 1) == 1)
        {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => CloseAll();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => CloseAll();
    }

    /// <summary>登録されている全ハンドルを閉じる。例外は握りつぶして次のハンドルへ進む。</summary>
    public static void CloseAll()
    {
        foreach (var key in Handles.Keys)
        {
            try
            {
                (key as IDisposable)?.Dispose();
            }
            catch
            {
                // フェイルセーフ優先: 1 つの失敗で他のハンドルの解放を止めない
            }
        }
    }

    internal static void Register(WinDivertHandle handle) => Handles.TryAdd(handle, 0);

    internal static void Unregister(WinDivertHandle handle) => Handles.TryRemove(handle, out _);
}
