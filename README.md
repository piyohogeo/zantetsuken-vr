# WORLD CUTTER -斬鉄剣-

VR空間の物を、刀の斬撃で好きな方向に両断するアクションゲームです。Unityで作っています。

https://github.com/user-attachments/assets/51af708f-045b-453e-8d21-c7a165f46077

PCVR（Meta Quest Link）でStanford Bunnyを切っている様子です（16秒）。

## 概要

斬撃の見た目の反応を、メッシュと物理の切断が終わるより先に返すことを中心に設計しています。

- **斬ったその場で切り口を描く**：刀の斬撃波が当たると、まずGPUで仮の切断を描きます。メッシュの切断と物理形状（Convex）の切断は、バックグラウンドのワーカーで並行して行い、終わり次第差し替えます。
- **何度でも切れる**：切った破片をさらに切れます。破片は独立した物理物体として動きます。
- **離れた部分は別の物体になる**：一つの物体のうち、空間的に分かれる部分を探して、二つの物体に分けます。探し方は、平面、折れ曲がった仕切り、グラフの3段です。
- **描画**：切った本体はGPUで選別し、間接描画で描きます。Windows（Direct3D 12）とQuest（Vulkan）では、ネイティブのプラグインが描きます。
- **対象**：PCVR（Meta Quest Link、90 Hz）、Meta Quest 3 / 3S の単体版、Androidの非VR版。

| 項目 | 内容 |
| --- | --- |
| エンジン | Unity 6000.3.22f1（Unity 6.3 LTS） |
| 描画 | URP |
| XR | OpenXR |
| スクリプト | C#、IL2CPP、Burst |

## PCVR版（Meta Quest Link）

街とNPCを含む通常プレイ版をWindows x64向けに配布します。プレリリース **0.1.1-alpha.2**（`pcvr-v0.1.1-alpha.2`）の配布情報は [GitHub Releases](https://github.com/piyohogeo/zantetsuken-vr/releases) を参照してください。人型キャラクターの切断・欠損表現を含みます。

展開したフォルダーの `Zantetsu.exe` から起動します。日本語の `起動設定.exe` で描画方式を選べます。導入・操作・対応環境は [PCVR版の利用案内](docs/pcvr.md)、確認した構成と測定条件は [PCVR性能評価](docs/pcvr-performance-2026-11-08.md)、任意の統計送信は [PCVR版プライバシーポリシー](docs/pcvr-privacy-policy.md) にまとめています。

起動用exeは未署名のため、SmartScreenの警告が表示される場合があります。配布元とアプリ名を確認したうえで「詳細情報」→「実行」を選ぶ手順は、[SmartScreenが表示された場合](docs/pcvr.md#smartscreenが表示された場合) を参照してください。

## Android版（非VR）

Android 12L（API 32）以降・ARM64・Vulkan対応端末向けの署名付きAPKを公開しています。現在のプレリリースは [0.1.0-alpha.1（versionCode 7）](https://github.com/piyohogeo/zantetsuken-vr/releases/tag/android-v0.1.0-alpha.1) です。[APKをダウンロード](https://github.com/piyohogeo/zantetsuken-vr/releases/download/android-v0.1.0-alpha.1/android-github-0.1.0-alpha.1-7.apk)できます。

縦画面・横画面に対応しています。左下の仮想ジョイスティックで前後移動と旋回、画面のフリックで切断します。Galaxy S25 UltraとPixel 6 Proで基本動作を確認しました。

導入・更新・操作方法と確認範囲は [Android版の利用案内](docs/android.md)、任意の統計送信については [プライバシーポリシー](docs/android-privacy-policy.md) を参照してください。

## このリポジトリでビルドできるもの

製品の都市や人物の素材（`Assets/Licensed`、`Assets/ThirdPartyPrivate`）はリポジトリに含めていません。
そのため、公開されているリポジトリをクローンしてビルドできるのは、**Stanford Bunnyの非商用デモ**だけです。
ビルドの入口は、素材がないことを見つけると、自動でデモに切り替えます。

デモでは、テーブルの上に並んだBunnyを刀で切ります。切り方は製品と同じで、切断、物理、空間分割、描画のすべてが製品と同じ経路です。

## ソースからのビルドに必要なもの

- Windows 10 / 11（x64）
- Unity 6000.3.22f1
  - モジュール「Windows Build Support (IL2CPP)」
- Visual Studio 2022
  - IL2CPPのビルドに「C++によるデスクトップ開発」が必要です。
- Meta Quest 3 / 3S と、PCの Meta Horizon Link アプリ（PCVRで遊ぶ場合）
- Direct3D 12 が使えるGPU（ネイティブ描画を使う場合）

## Stanford Bunny デモのビルド（PCVR）

PowerShellで、リポジトリのルートから実行します。

```powershell
.\Tools\Build-ScenarioPlayer.ps1 -BuildMode Demo -Output D:\builds\bunny-pcvr
```

- `-Output` には、まだ何もない新しいフォルダを指定してください。
- `CutWorldSandbox.exe`（Windows x64、IL2CPP、Development）と、起動用の `Zantetsu.exe`、日本語の `起動設定.exe` ができます。
  - 同じフォルダに、内容の監査 `zantetsu-content-audit.json` とStanfordの出典の通知が出力されます。
- PlayerにはDirect3D 12とDirect3D 11の両方が入ります。`Zantetsu.exe` は、単一のRadeonを判定できた場合はDirect3D 11、それ以外はDirect3D 12を自動で選びます。`起動設定.exe` で手動選択もできます。
  - `-Direct3D12` は既存のビルド手順との互換用に受け付けます。現在の既定もDirect3D 12、Direct3D 11の順です。
- ビルドの間に、Unityが次の3つのファイルを書き換えます。
  - `Assets/NonCommercialDemo/StanfordBunny/StanfordBunnyDemo.unity`
  - `Assets/NonCommercialDemo/StanfordBunny/BunnyProfile.asset`
  - `Assets/Settings/PC_RPAsset.asset`
- 変更を残す必要がなければ、ビルドのあとで戻してください。

```powershell
git checkout -- Assets/NonCommercialDemo/StanfordBunny/StanfordBunnyDemo.unity Assets/NonCommercialDemo/StanfordBunny/BunnyProfile.asset Assets/Settings/PC_RPAsset.asset
```

## Stanford Bunny デモの実行（PCVR）

1. PCに Meta Horizon Link アプリを入れ、OpenXRの既定のランタイムを Meta Horizon Link にします。
2. QuestをLinkで接続します（USBケーブルか Air Link）。ヘッドセットの中でLinkを有効にします。
3. `Zantetsu.exe` を起動します。引数は要りません。
   - OpenXRは自動で始まります。
   - ログを残すなら `-logFile <ファイル>` を付けます。
4. 遊び方
   - 右手の刀を振って、Bunnyを切ります。
   - 左コントローラのメニューボタンで、メニューを開きます。
   - 右手のレイとトリガーで `Bunny count`（1〜20）を選び、`Reset scene` を押すと、その数で作り直します。
   - 音量、移動速度、回転の設定も、同じメニューで変えられます。
5. 終わるときは、PCでPlayerのウィンドウを閉じます。

ヘッドセットがつながっていない場合は、デスクトップの画面で動きます。キーボードのEscapeでメニューを開きます。

Questの単体版（APK）のビルド、Bunnyのデータを元データから作り直す手順、ビルドの境界の詳細は、[Tools/StanfordBunny/README.md](Tools/StanfordBunny/README.md) を参照してください。
確かめた内容の記録は [Tools/StanfordBunny/VERIFICATION.md](Tools/StanfordBunny/VERIFICATION.md) にあります。

## ディレクトリ

| 場所 | 内容 |
| --- | --- |
| `Assets/Zantetsu/Runtime` | ゲーム本体（切断、物理、描画、入力、シナリオ） |
| `Assets/Zantetsu/Editor` | ビルドの入口と、Editorの道具 |
| `Assets/Zantetsu/Tests` | EditModeとPlayModeの試験 |
| `Assets/NonCommercialDemo/StanfordBunny` | Stanford Bunnyデモのシーンとデータ |
| `Tools/` | ビルド、検査、データ作成の台本 |
| `docs/` | 配布の手順、診断の記録 |
| `DESIGN.md` | 技術設計書と決定の記録 |

## ライセンス

このリポジトリは [MIT License](LICENSE) です（Copyright (c) 2026 hogehogeo）。ゲームの名前とロゴも含みます。

ただし、次のものはMITではなく、それぞれの条件に従います。

- **Stanford Bunny**：Stanford University Computer Graphics Laboratory の [3D Scanning Repository](https://graphics.stanford.edu/data/3Dscanrep/) のデータです。
  - 対象は、元データとそこから作ったもの（修復したメッシュ、Convex、.blend、Unityへの書き出し）です。
  - 置き場所は `Assets/NonCommercialDemo/StanfordBunny/` と、`Tools/StanfordBunny/` の `source/` と `generated/` です。
  - Stanfordの非商用の条件が適用され、デモは非商用でだけ使えます。
  - 条件の要約は [STANFORD-NOTICE.txt](Assets/NonCommercialDemo/StanfordBunny/STANFORD-NOTICE.txt) にあります。
  - README の動画も、Bunnyを使ったデモの映像です。
- **Bunnyの準備に使う第三者の道具**：範囲は [Tools/StanfordBunny/THIRD-PARTY-NOTICES.md](Tools/StanfordBunny/THIRD-PARTY-NOTICES.md) を参照してください。
  - Convexアドオン（`Tools/StanfordBunny/addons/convex_decomposition.py`）：Apache-2.0
  - V-HACD（`Tools/StanfordBunny/vhacd/`）：BSD-3-Clause
- **Unityのプロジェクトテンプレートのファイル**（`Assets/TutorialInfo/`）：Unityの条件に従います。
- **通常プレイ版に組み込む都市・人物素材**は、提供元のライセンス条件に従います。PCVR配布版の扱いは [PCVR版の利用案内](docs/pcvr.md#素材とライセンス) を参照してください。
- **アプリに含まれる第三者のソフトウェア**：各配布物の第三者表記に従います。Android版ではAPKに同梱し、Releaseにも `ThirdPartyNotices.txt` を添付します。参照先は [Android版の利用案内](docs/android.md) にまとめています。
