# 同梱する WinDivert のバージョン・取得元・既知ハッシュを 1 か所で定義する。
# fetch-windivert.ps1（取得と配置）と publish.ps1（配布物の検査）が dot-source する。
#
# 注意: ここのハッシュを変えたら src\NetworkTrafficController.Core\Native\InstallDirectoryGuard.cs の KnownHashes も同時に更新すること
# （アプリは起動時に exe の隣の WinDivert が同梱版と一致するかを同じ値で確認する）。

$WinDivertRelease = '2.2.2'
$WinDivertPackage = "WinDivert-$WinDivertRelease-A"
$WinDivertZipUrl = "https://github.com/basil00/WinDivert/releases/download/v$WinDivertRelease/$WinDivertPackage.zip"

# zip 内の x64\ にある配布物（exe の隣に置く 2 ファイル）の SHA-256
$WinDivertKnownHashes = @{
    'WinDivert.dll'   = 'c1e060ee19444a259b2162f8af0f3fe8c4428a1c6f694dce20de194ac8d7d9a2'
    'WinDivert64.sys' = '8da085332782708d8767bcace5327a6ec7283c17cfb85e40b03cd2323a90ddc2'
}
