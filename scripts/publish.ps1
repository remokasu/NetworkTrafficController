<#
.SYNOPSIS
  NetworkTrafficController を配布用に publish する（自己完結・単一ファイル、win-x64）。

.DESCRIPTION
  dotnet publish を自己完結（--self-contained）で実行する。RID（win-x64）・単一ファイル・単一ファイル内の圧縮は
  App.csproj で指定済み。配布向けの設定（デバッグシンボル・XML ドキュメントを含めない）と、packages.lock.json との
  一致を強制する locked mode をここで加える。
  出力先には NetworkTrafficController.exe と、exe の隣に置く必要があるファイル（WinDivert.dll / WinDivert64.sys / WPF のネイティブ DLL /
  LICENSE / LICENSE.WinDivert）が並ぶ。WinDivert のハッシュが同梱版（InstallDirectoryGuard の既知値）と一致するかも確認する。

.PARAMETER Output
  出力先フォルダ（既定: リポジトリ直下の publish\NetworkTrafficController。開発物の NetworkTrafficController フォルダの外に出して、配布物と分ける）。

#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\..\publish\NetworkTrafficController')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

# RID（win-x64）・単一ファイル・圧縮は App.csproj で指定している。ここで -r / -p: として渡すとグローバルプロパティとして
# Core にも効き、packages.lock.json と食い違って locked mode の復元に失敗する
$common = @(
    '-c', 'Release',
    '--self-contained',
    '-p:RestoreLockedMode=true',               # packages.lock.json と違うパッケージが解決されたら失敗させる
    '-p:DebugType=none',                       # 配布物に .pdb を含めない
    '-p:PublishDocumentationFiles=false',      # 配布物に .xml を含めない
    '-o', $Output,
    '--nologo'
)

# 毎回クリーンビルドにする。SDK 10.0.400 の増分 publish は 2 回目以降に WPF のネイティブ DLL（*_cor3.dll）を
# 「今回の出力に無いファイル」と誤判定して出力先から削除する（_IncrementalCleanPublishDirectory。2026-10-02 に再現）。
# App の Release 中間生成物（前回の出力一覧 PublishOutputs.*.txt を含む）を消せば削除は起きない
$appObjRelease = Join-Path $root 'src\NetworkTrafficController.App\obj\Release'
if (Test-Path $appObjRelease) {
    Write-Host "Removing $appObjRelease (clean publish)"
    Remove-Item -Recurse -Force $appObjRelease
}

Write-Host "Publishing NetworkTrafficController (GUI) -> $Output"
dotnet publish (Join-Path $root 'src\NetworkTrafficController.App\NetworkTrafficController.App.csproj') @common
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (NetworkTrafficController.App) failed: $LASTEXITCODE" }

# exe 本体が生成されたか確認する（dotnet publish が 0 で終わっても出力が欠けていないことを保証する）
# WPF のネイティブ DLL（*_cor3.dll / vcruntime140_cor3.dll）は単一ファイルに埋め込まれず exe の隣に必要。
# 欠けると起動直後に DllNotFoundException で落ちる（2026-10-02 に増分 publish で欠けたまま出力された）
$required = @(
    'NetworkTrafficController.exe', 'LICENSE', 'LICENSE.WinDivert',
    'D3DCompiler_47_cor3.dll', 'PenImc_cor3.dll', 'PresentationNative_cor3.dll', 'vcruntime140_cor3.dll', 'wpfgfx_cor3.dll'
)
foreach ($name in $required) {
    if (-not (Test-Path (Join-Path $Output $name))) { throw "$name が出力にありません" }
}

# WinDivert の配布物が同梱版（scripts\windivert-known.ps1 で定義。2.2.2-A x64）と一致するか確認する
. (Join-Path $PSScriptRoot 'windivert-known.ps1')
$known = $WinDivertKnownHashes
foreach ($name in $known.Keys) {
    $path = Join-Path $Output $name
    if (-not (Test-Path $path)) { throw "$name が出力にありません: $path" }
    $hash = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $known[$name]) { throw "$name のハッシュが同梱版と一致しません: $hash" }
    Write-Host "OK  $name  $hash"
}

Write-Host ''
Write-Host 'Output files:'
Get-ChildItem $Output | Sort-Object Name | Format-Table Name, @{ n = 'Size(KB)'; e = { [math]::Round($_.Length / 1KB) } } -AutoSize
