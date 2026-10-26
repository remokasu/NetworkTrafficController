using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace NetworkTrafficController.Core.Native;

/// <summary>
/// グローバルホットキーを 1 つ登録し、押されるたびにコールバックを呼ぶ（SPEC: 対象アプリにフォーカスを置いたまま Block を ON/OFF する）。
/// <c>RegisterHotKey(hWnd = NULL)</c> で登録し、専用スレッドのメッセージループで <c>WM_HOTKEY</c> を受ける。
/// 登録・ループ・解除はすべて同じスレッドで行う（<c>UnregisterHotKey</c> は登録したスレッドから呼ぶ必要がある）。
/// </summary>
/// <remarks>
/// 実機（キー入力）でしか検証できないため、ここはユニットテスト対象外。登録の成否はログと <see cref="Registered"/> で観測する。
/// </remarks>
public sealed class HotkeyListener : IDisposable
{
    private const int HotkeyId = 1;

    private readonly HotkeyCombo _combo;
    private readonly Action _onPressed;
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _threadId;
    private int _disposed;

    /// <summary>ホットキー・押下時の動作・ロガーを指定して生成する（<see cref="Start"/> で登録）。</summary>
    public HotkeyListener(HotkeyCombo combo, Action onPressed, ILogger logger)
    {
        _combo = combo;
        _onPressed = onPressed;
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "network-traffic-controller-hotkey" };
        _thread.TrySetApartmentState(ApartmentState.STA);
    }

    /// <summary>登録できたか（<see cref="Start"/> の完了後に有効）。</summary>
    public bool Registered { get; private set; }

    /// <summary>スレッドを開始してホットキーを登録する。登録の成否が確定するまで待つ。</summary>
    public bool Start()
    {
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
        return Registered;
    }

    private unsafe void Run()
    {
        _threadId = User32Native.GetCurrentThreadId();
        Registered = User32Native.RegisterHotKey(nint.Zero, HotkeyId, (HotkeyModifiers)_combo.Modifiers, _combo.VirtualKey);
        if (!Registered)
        {
            _logger.LogWarning("RegisterHotKey('{Combo}') failed: Win32 error {Error} (another app may have registered the same key)", _combo.Text, Marshal.GetLastPInvokeError());
            _ready.TrySetResult(false);
            return;
        }

        _logger.LogInformation("Global hotkey registered: {Combo}", _combo.Text);
        _ready.TrySetResult(true);

        User32Native.Msg msg;
        while (true)
        {
            int result = User32Native.GetMessage(&msg, nint.Zero, 0, 0);
            if (result is 0 or -1) // WM_QUIT または失敗
            {
                break;
            }

            if (msg.Message == User32Native.WmHotkey && (int)msg.WParam == HotkeyId)
            {
                try
                {
                    _onPressed();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "hotkey callback threw");
                }
            }
        }

        User32Native.UnregisterHotKey(nint.Zero, HotkeyId);
        _logger.LogInformation("Global hotkey unregistered: {Combo}", _combo.Text);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Start() 完了前に Dispose された場合でも Registered / _threadId の可視性を保証する（happens-before）
        _ready.Task.Wait();
        if (Registered && _threadId != 0)
        {
            User32Native.PostThreadMessage(_threadId, User32Native.WmQuit, 0, nint.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
