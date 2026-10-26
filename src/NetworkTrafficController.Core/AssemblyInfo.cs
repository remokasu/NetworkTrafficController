using System.Runtime.InteropServices;

// P/Invoke の既定探索を System32 に限定する（exe フォルダ・カレントディレクトリ・PATH からの DLL 差し替え対策）。
// WinDivert.dll は NativeLibraryLoader が絶対パスで読み込むので、この属性の影響を受けない（ADR-0004 V14）。
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
