# NetworkTrafficController

Windows 用の、**プロセス単位の通信モニタリング＆制御ツール**。

どのプロセスがどれだけ通信しているかをリアルタイムに表示する。また、特定のプロセスの通信だけを切断・速度制限・劣化することができる。

パケットの取得と操作には [WinDivert 2.2.2](https://reqrypt.org/windivert.html) を使う。

## 機能

**モニタリング**

- プロセスごとの送受信速度・累計通信量・接続数（列ごとにソート可、名前・PID で絞り込み可）
- 選択したプロセスの接続一覧（プロトコル、ローカル / リモートの IP:ポート、送受信量、loopback か）
- 速度の時系列グラフ（全体 / 選択プロセス）
- TCP / UDP、送信 / 受信、loopback（127.0.0.1）に対応

**制御**（プロセス一覧から対象を選び、チェックを入れた瞬間に効く）

| 機能 | 内容 |
|---|---|
| Block | 対象の全パケットを破棄する（無音で切断し、タイムアウトさせる） |
| Reset | TCP 接続に RST を注入して即時に切断する（UDP と loopback には効かない） |
| 帯域制限 | 上り・下りを個別に KB/s・MB/s・Mbps で指定する（0 は停止） |
| Drop | 指定した確率でパケットを破棄する |
| Delay | 固定遅延と揺らぎ（jitter）を加える。同じ接続の中では順序を保つ |
| 方向 | Block / Reset / Drop / Delay の各行の ↑↓ で送信 / 受信を個別に指定する。帯域は Upload / Download の欄で向きが決まる |
| 時限 Block | 指定した秒数だけ切断し、自動で元に戻す |

監視は通信を横取りしない読み取り専用のモードで動く。制御は、対象を選んだときだけ有効になる。対象と照合できないパケットはそのまま通すので、対象外のプロセスの通信は止まらない。

## 動作環境

- Windows 10 / 11（x64）
- 管理者権限（WinDivert のドライバ読み込みに必須。起動時に UAC の確認が出る）
- ビルドには .NET 10 SDK（10.0.400 で確認）が必要。publish した exe は自己完結型なので、実行する PC に .NET は不要。

## ビルド

1. .NET 10 SDK を入れる。バージョンは `global.json` で固定している（10.0.400）。
2. WinDivert の配布物を `native/` に置く。`scripts/fetch-windivert.ps1` が公式リリース（GitHub の basil00/WinDivert v2.2.2）から zip を取得し、`WinDivert.dll` と `WinDivert64.sys` を SHA-256 で照合してから配置する。すでに置いてあれば何もしない。
3. ビルドとテストを実行する。

```powershell
.\scripts\fetch-windivert.ps1
dotnet build NetworkTrafficController.sln -c Release
dotnet test NetworkTrafficController.sln -c Release
```

NuGet の依存や SDK のバージョンを変えたら、`dotnet restore NetworkTrafficController.sln --force-evaluate` で `packages.lock.json` を作り直し、差分を確認してコミットする。CI は locked mode で復元するので、ロックファイルの更新漏れはビルドの失敗として現れる。

配布用の exe（自己完結・単一ファイル、win-x64）は `scripts/publish.ps1` で作る。出力先は `-Output` で指定する（省略するとリポジトリの親フォルダの `publish/NetworkTrafficController` に出る）。exe と同じフォルダに `WinDivert.dll`、`WinDivert64.sys`、WPF のネイティブ DLL が並ぶ。

```powershell
.\scripts\publish.ps1 -Output .\publish
```

## リリース

`vX.Y.Z` のタグを push すると、GitHub Actions（`.github/workflows/release.yml`）がテストと publish を行い、`NetworkTrafficController-X.Y.Z-win-x64.zip` と `SHA256SUMS.txt` を GitHub Release に添付する。タグは `Directory.Build.props` の `<Version>` と一致していなければならない。

## 検証

配布物に何が入っているかは、次の方法で確かめられる。

**zip の出所**

- GitHub の build provenance attestation で、その zip が「このリポジトリのこのコミットから、`release.yml` によって作られた」ことを確かめる（GitHub CLI が必要）。検証の対象は zip であり、展開した exe 単体では検証できない。

  ```powershell
  gh attestation verify .\NetworkTrafficController-X.Y.Z-win-x64.zip --repo remokasu/NetworkTrafficController
  ```

- `SHA256SUMS.txt` の値と手元の zip のハッシュを照合すると、ダウンロードが壊れたり途中で差し替わったりしていないことを確かめられる。この値は同じワークフローが出すものなので、出所の証明は上の attestation に任せる。

  ```powershell
  Get-FileHash .\NetworkTrafficController-X.Y.Z-win-x64.zip -Algorithm SHA256
  ```

**exe に入るもの**

- NuGet のライブラリは `packages.lock.json` で固定し、CI は locked mode で復元する。ロックと違うパッケージが解決されるとビルドは失敗する。
- 自己完結 exe に同梱される .NET ランタイムはロックファイルの対象外で、`global.json` で固定した SDK のバージョンで決まる。
- `WinDivert.dll` と `WinDivert64.sys` は開発元の公式リリース（basil00/WinDivert v2.2.2）のものをそのまま使う。CI は取得時に SHA-256 を照合する。アプリも起動時に exe の隣の 2 ファイルを同じ値で照合し、違えばログに警告を書く（読み込み自体は止めない）。ハッシュは `scripts/windivert-known.ps1` にある。
- exe はコード署名していない。同じソース・ロックファイル・SDK から、上記の手順で同じ構成の exe を手元で作れる。

**動作**

- このツール自身は外部へ通信しない。更新確認やテレメトリも無い。`src/` に `HttpClient` や `Socket` などの送信用の型は使っていないので、ソースで確かめられる。
- 管理者権限が必要なのは WinDivert のドライバを読み込むためで、それ以外の権限昇格の用途は無い。
- WinDivert はパケットを捕捉するドライバなので、一部のウイルス対策エンジンがヒューリスティックで検出することがある。

