<#
.SYNOPSIS
  WinDivert の配布物（WinDivert.dll / WinDivert64.sys）を native\ に置く。

.DESCRIPTION
  native\ の 2 ファイルが同梱版（scripts\windivert-known.ps1）の SHA-256 と一致していれば何もしない。
  無い、または一致しなければ、公式リリースの zip を native\_download\ に取得して展開し、x64\ の 2 ファイルの
  ハッシュを照合してから native\ にコピーする。照合は展開先で先に済ませるので、不一致のときに native\ は変わらない。
  ビルドの前提として、開発者のローカルと GitHub Actions の両方で使う。

.PARAMETER Force
  native\ の内容に関わらず取得と配置をやり直す。
#>
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest の進捗表示を止める（Windows PowerShell 5.1 では極端に遅くなる）

. (Join-Path $PSScriptRoot 'windivert-known.ps1')
if (-not $WinDivertKnownHashes -or $WinDivertKnownHashes.Count -eq 0) { throw 'windivert-known.ps1 の $WinDivertKnownHashes が空です' }   # 空だと検証が素通りする（fail-open）のを防ぐ

$native = (Resolve-Path (Join-Path $PSScriptRoot '..\native')).Path
$downloadAttempts = 3

function Get-Sha256([string]$Path) {
    (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# $Dir にある 2 ファイルが既知ハッシュと一致しなければ、その理由を返す（一致すれば $null）
function Get-KnownFilesMismatch([string]$Dir) {
    foreach ($name in $WinDivertKnownHashes.Keys) {
        $path = Join-Path $Dir $name
        if (-not (Test-Path $path)) { return "$name がありません: $path" }
        $hash = Get-Sha256 $path
        if ($hash -ne $WinDivertKnownHashes[$name]) {
            return "$name の SHA-256 が同梱版と一致しません: $hash（期待値 $($WinDivertKnownHashes[$name])）"
        }
    }
    return $null
}

if (-not $Force -and -not (Get-KnownFilesMismatch $native)) {
    Write-Host "WinDivert $WinDivertRelease is already in $native"
    exit 0
}

$download = Join-Path $native '_download'
New-Item -ItemType Directory -Force -Path $download | Out-Null
$zip = Join-Path $download "$WinDivertPackage.zip"
$tmp = "$zip.tmp"

# 一時ファイルに落としてから改名する（途中で失敗した zip を次回に拾わない）。一時的なネットワーク失敗はリトライする
Write-Host "Downloading $WinDivertZipUrl"
for ($attempt = 1; $attempt -le $downloadAttempts; $attempt++) {
    try {
        if (Test-Path $tmp) { Remove-Item $tmp -Force }
        Invoke-WebRequest -Uri $WinDivertZipUrl -OutFile $tmp
        Move-Item $tmp $zip -Force
        break
    }
    catch {
        if ($attempt -eq $downloadAttempts) { throw }
        Write-Warning "Download failed (attempt $attempt/$downloadAttempts): $($_.Exception.Message)"
        Start-Sleep -Seconds (5 * $attempt)
    }
}

Write-Host "Extracting to $download"
Expand-Archive -Path $zip -DestinationPath $download -Force

$extracted = Join-Path $download "$WinDivertPackage\x64"
$mismatch = Get-KnownFilesMismatch $extracted
if ($mismatch) { throw $mismatch }

foreach ($name in $WinDivertKnownHashes.Keys) {
    Copy-Item (Join-Path $extracted $name) (Join-Path $native $name) -Force
    Write-Host "OK  $name  $($WinDivertKnownHashes[$name])"
}
