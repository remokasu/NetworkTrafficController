using System.Globalization;

namespace NetworkTrafficController.Core.Native;

/// <summary>ホットキーの組み合わせ（修飾キー + 仮想キーコード）。"ctrl+alt+b" のような文字列から作る。</summary>
public readonly record struct HotkeyCombo(uint Modifiers, uint VirtualKey, string Text)
{
    /// <summary>"ctrl+shift+f9" のような文字列を解析する。修飾キーは ctrl / alt / shift / win。</summary>
    /// <exception cref="FormatException">書式が不正な場合。</exception>
    public static HotkeyCombo Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        uint mods = (uint)HotkeyModifiers.NoRepeat; // 押しっぱなしの連続発火を抑える
        uint vk = 0;
        string? keyName = null;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    mods |= (uint)HotkeyModifiers.Control;
                    break;
                case "alt":
                    mods |= (uint)HotkeyModifiers.Alt;
                    break;
                case "shift":
                    mods |= (uint)HotkeyModifiers.Shift;
                    break;
                case "win":
                    mods |= (uint)HotkeyModifiers.Win;
                    break;
                default:
                    if (keyName is not null)
                    {
                        throw new FormatException($"A hotkey can have only one key: '{text}'");
                    }

                    keyName = raw;
                    vk = ToVirtualKey(raw);
                    break;
            }
        }

        if (vk == 0)
        {
            throw new FormatException($"No key specified in hotkey: '{text}' (e.g. ctrl+alt+b, f9)");
        }

        return new HotkeyCombo(mods, vk, text);
    }

    private static uint ToVirtualKey(string key)
    {
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return c; // VK は ASCII の 'A'-'Z' / '0'-'9' と一致する
            }
        }

        // ファンクションキー F1〜F24（VK_F1 = 0x70）
        if ((key[0] is 'f' or 'F') && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 24)
        {
            return (uint)(0x70 + n - 1);
        }

        throw new FormatException($"Unsupported key: '{key}' (A-Z, 0-9, F1-F24 are supported)");
    }
}
