using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using NetworkTrafficController.Core.Control;
using NetworkTrafficController.Core.Native;

namespace NetworkTrafficController.App.Services;

/// <summary>
/// 「グローバルホットキーで Block の ON/OFF を切り替える」機能のまとまり（SPEC 機能要件 10）。
/// 2026-09-29 のユーザー判断で GUI からは外したが、復活できるようにここに集約して残している。
/// </summary>
/// <remarks>
/// 復活させる手順:
/// 1. MainViewModel に <see cref="BlockHotkey"/> のフィールドを持たせ、コンストラクタで生成する（<c>new BlockHotkey(host.Store, logger)</c>）。
/// 2. 登録ボタンのコマンドから <see cref="Register"/> を呼び、戻り値のメッセージを画面に出す。
/// 3. MainViewModel.Dispose で <see cref="Dispose"/> を呼ぶ。設定の保存機能は 2026-10-01 に削除したので、キーを覚えさせたい場合は別途用意する。
/// Core 側の <see cref="HotkeyListener"/> / <see cref="HotkeyCombo"/> は残してある（2026-09-30 にコンソール版の <c>--hotkey</c> も外した）。
/// </remarks>
public sealed class BlockHotkey : IDisposable
{
    private readonly ControlPolicyStore _store;
    private readonly ILogger _logger;
    private HotkeyListener? _listener;

    /// <summary>ポリシーストアとロガーを指定して生成する。</summary>
    public BlockHotkey(ControlPolicyStore store, ILogger logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>登録中のホットキー（未登録なら null）。</summary>
    public string? Registered { get; private set; }

    /// <summary>
    /// ホットキーを登録する（既に登録していれば置き換える）。押されるたびに全対象への手動 Block を反転し、
    /// <paramref name="onToggled"/> を UI スレッドで呼ぶ。
    /// </summary>
    /// <returns>画面に出すメッセージ。</returns>
    public string Register(string text, Action<bool> onToggled)
    {
        Unregister();
        HotkeyCombo combo;
        try
        {
            combo = HotkeyCombo.Parse(text);
        }
        catch (FormatException ex)
        {
            return "Invalid hotkey format: " + ex.Message;
        }

        var dispatcher = Dispatcher.CurrentDispatcher;
        var listener = new HotkeyListener(combo, () => dispatcher.BeginInvoke(() => onToggled(_store.ToggleManualBlock())), _logger);
        if (!listener.Start())
        {
            listener.Dispose();
            return $"Could not register hotkey {combo.Text} (another app may be using it)";
        }

        _listener = listener;
        Registered = combo.Text;
        return $"Registered hotkey {combo.Text}";
    }

    /// <summary>登録を解除する。</summary>
    public void Unregister()
    {
        _listener?.Dispose();
        _listener = null;
        Registered = null;
    }

    /// <inheritdoc />
    public void Dispose() => Unregister();
}
