using System.Runtime.InteropServices;

namespace NetworkTrafficController.Core.Native;

/// <summary>グローバルホットキーの修飾キー（<c>MOD_*</c>）。</summary>
[Flags]
internal enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000,
}

/// <summary>user32.dll のグローバルホットキー API（<c>RegisterHotKey</c> / <c>UnregisterHotKey</c> とスレッドメッセージ）。</summary>
internal static unsafe partial class User32Native
{
    private const string LibraryName = "user32.dll";

    public const uint WmHotkey = 0x0312;
    public const uint WmQuit = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    /// <summary>hWnd = NULL で登録すると、<c>WM_HOTKEY</c> は呼び出しスレッドのメッセージキューに届く。</summary>
    [LibraryImport(LibraryName, EntryPoint = "RegisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, HotkeyModifiers fsModifiers, uint vk);

    [LibraryImport(LibraryName, EntryPoint = "UnregisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport(LibraryName, EntryPoint = "GetMessageW", SetLastError = true)]
    public static partial int GetMessage(Msg* lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport(LibraryName, EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint idThread, uint msg, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    public static partial uint GetCurrentThreadId();
}
