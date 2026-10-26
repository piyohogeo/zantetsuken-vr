# Stanford Bunny 非商用デモ

## ビルド

Unity 6000.3.22f1 を使用します。元データ、生成済みUnity入力、シーンを含むので、通常のビルドにBlenderやネット接続は不要です。
Stanford公式の未変更アーカイブ `source/bunny.tar.gz` と、レシピが読む `source/bun_zipper.ply` を保存しています。
アーカイブSHA-256: `a5720bd96d158df403d153381b8411a727a1d73cff2f33dc9b212d6f75455b84`。

Windows:

```powershell
.\Tools\Build-ScenarioPlayer.ps1                         # Auto: privateなしならBunny
.\Tools\Build-ScenarioPlayer.ps1 -BuildMode Demo         # privateがあってもBunny
.\Tools\Build-ScenarioPlayer.ps1 -BuildMode Commercial   # private必須、Bunnyへのfallback禁止
```

Quest:

```powershell
.\Tools\Quest3s\Build-ScenarioPlayer.ps1 -BuildMode Auto
.\Tools\Quest3s\Build-ScenarioPlayer.ps1 -BuildMode Demo -Development
.\Tools\Quest3s\Build-ScenarioPlayer.ps1 -BuildMode Commercial
```

Unity直接呼び出しは `-zantetsuBuildMode auto|demo|commercial`。省略はauto。
Autoは `Assets/Licensed` / `Assets/ThirdPartyPrivate` に実データがない場合にだけデモへ切り替えます。
privateが一部だけ存在する場合はビルドを失敗させます。明示的Demoでデモを選べます。
Questデモは通常製品と異なる application ID `com.zantetsuken.quest.bunnydemo` を使います。
デモは `play` シナリオ専用です。測定用シナリオを指定すると、そのシーンがないことを報告して終了します。

左コントローラのメニューボタン（PCはEscape）で開き、右コントローラのレイ＋トリガーで
`Bunny count`（1～20、初期3）を選び `Reset scene` を押します。スライダーだけでは再生成しません。
リセットは既存の切断処理を終了し、世界の解放を確認してから全てのBunnyと切断片を作り直します。
音量・移動速度・回転の既存設定も使用できます。
切断インパルスは既存のplay profileと同じ `J = 1 N·s/kg × 各断片の質量`。各自由断片に切断面の法線方向へ約1 m/sを一度加えます。

## PCVR（Meta Quest Link）で実行

Windows Playerは起動と同時にOpenXRを開始します（XR Managementの初期化 ON）。
有効なOpenXRランタイムがあればPCVR、なければデスクトップ表示で `play` を始めます。

### ビルド

- `Assets/Licensed` などのprivateデータがないクローン（公開リポジトリそのもの）なら、`-BuildMode Auto` でも `Demo` でもBunnyになります。
- privateデータのある木では、`-BuildMode Demo` を明示してください。
- 出力先は、既存の成果物がない新しいフォルダにしてください。

```powershell
.\Tools\Build-ScenarioPlayer.ps1 -BuildMode Demo -Direct3D12 -Output D:\builds\bunny-pcvr
```

- 成果物は `CutWorldSandbox.exe` です。同じフォルダに内容監査 `zantetsu-content-audit.json` とStanfordの通知 `...STANFORD-NOTICE.txt` が出ます。
- `-Direct3D12` を付けると、このビルドに限ってWindowsのグラフィックスAPIが「Direct3D 12、Direct3D 11」の順になります。
  - Unityには `-zantetsuPlayerD3D12` が渡ります。
  - PlayerはDirect3D 12で起動し、本体をネイティブのプラグインで描きます（VP3C-NATIVE、D-215の既定）。
  - Direct3D 12が使えないPCでは、Direct3D 11（Unityの呼び出し）に下がります。
  - プロジェクトの設定（Direct3D 11だけ）は、ビルドの後に元へ戻ります。ビルドのログに `graphics APIs back to Direct3D11` と出ます。
- `-Direct3D12` を付けない場合は、プロジェクトの設定どおりDirect3D 11だけのPlayerになります。
- このスクリプトが作るのはDevelopment Playerです。
- Unityを直接呼んで `-zantetsuPlayerRelease` を付けるとRelease Playerになります。ただし、ReleaseでのPCVRの確認はまだしていません。
- ビルドの間に、Unityが次の3ファイルを書き換えます。
  - Bunnyシーンは毎回作り直して保存されます。内容は同じで、IDと並びだけが変わります。
  - `BunnyProfile.asset` は今のコードの項目で保存し直されます。
  - `PC_RPAsset.asset` はシェーダの絞り込みの設定が書き換わります。
- コミットする変更でなければ、ビルドの後で戻してください。

```powershell
git checkout -- Assets/NonCommercialDemo/StanfordBunny/BunnyProfile.asset Assets/NonCommercialDemo/StanfordBunny/StanfordBunnyDemo.unity Assets/Settings/PC_RPAsset.asset
```

### 実行

1. PCにMeta Horizon Linkアプリを入れ、OpenXRの既定ランタイムをMeta Horizon Linkにします。
   - 設定はレジストリ `HKLM\SOFTWARE\Khronos\OpenXR\1` の `ActiveRuntime` で、値が `...\oculus-runtime\oculus_openxr_64.json` になります。
2. QuestをLinkで接続します（USBケーブルかAir Link）。ヘッドセットの中でLinkを有効にしておきます。
3. `CutWorldSandbox.exe` を起動します。引数は要りません。
   - 記録を残すなら `-logFile <ファイル>` を付けます。
   - Meta XR Simulator用の環境変数（`XR_RUNTIME_JSON` など）が設定されていないシェルから起動してください。
4. 操作
   - 刀は右手です。
   - 左コントローラのメニューボタンでメニューを開きます。
   - 右手のレイとトリガーで `Bunny count` を選び、`Reset scene` を押します。
5. 終了はPCでPlayerのウィンドウを閉じます。

### 起動の確認（ログ）

| 行 | 意味 |
|---|---|
| `XR_SESSION_STATE_FOCUSED` と `Runtime Name: Oculus` | Linkでセッションが始まった |
| `ZANTETSU SCENARIO: play in StanfordBunnyDemo` | デモのシーンで `play` が始まった |
| `CUT PHYSICS STEP: mode Display (automatic) ... target rate 90 Hz from the XR display` | 物理がLinkの表示レートで動いている（PCVRの表示ステップ、D-255） |
| `STANFORD BUNNY NONCOMMERCIAL DEMO: <n> rabbits` | Bunnyの生成。リセットのたびに出る |
| `ZANTETSU LAUNCH: ... graphics=Direct3D12` | Direct3D 12で起動した（`-Direct3D12` のビルド） |
| `VP BODY ROUTE: VP3C-NATIVE -- ... drawn by the Direct3D 12 ExecuteIndirect plugin` | 本体をネイティブのプラグインで描いている |
| `VP BODY ROUTE (end): ... refused by the display 0, cameras not drawn for want of it 0` | 世界の終わりに出る。描画を断った数と、描けなかったカメラが0 |
| `VP BODY ROUTE: VP3C ... drawn by Unity's calls: the default on Direct3D11` | Direct3D 11のPlayerでの経路（`-Direct3D12` なし、またはDirect3D 12が使えないPC） |

検証の記録は [VERIFICATION.md](VERIFICATION.md) の「PCVR（Link）」を参照してください。

## 商用ビルドの境界

`Assets/NonCommercialDemo/**` はStanfordの非商用データです。Resources / StreamingAssetsには置きません。
Commercialはprivateシーンだけを指定し、デモシーン・間接依存・Resources・preloaded assetsをチェックします。
別の旧ビルド入口やUnityの通常Buildでも、Bunnyを含めるにはデモ用Scopeが必要です。
実際に処理されたシーンとBuildReportのpacked contentsも検査します。
明示DemoではprivateのResourcesも入れないため、`Assets/Licensed`, `Assets/ThirdPartyPrivate`,
`Assets/StreamingAssets/Quest3s` をビルド中だけ `.bunny-demo-content-staging` に退避し、終了時に.metaも含めて復元します。
プロセスが中断された場合は次のシナリオビルドが先に復元します。復元先に別データがある場合は上書きせず停止します。
退避フォルダを消さないでください。`.bunny-demo-content-staging/0` はLicensed、1はThirdPartyPrivate、2はQuest3sのバックアップです。
ビルド内容の監査JSONと、デモの場合にはStanfordの出典・条件通知を出力します。
以前のデモ成果物が残る出力先への商用ビルドは拒否します。商用には新しい出力先を使います。
このチェックを避けてデータを別の名前・形式に変換して商用に混入させないでください。
リポジトリの無料公開が許可されても、無料配布した商用宣伝物まで許可されるとは限りません。

## 元データから再生成

```powershell
.\Tools\StanfordBunny\Prepare-Bunny.ps1
```

Blender 4.5 LTS（検証は4.5.13）が必要です。全段階を `--factory-startup` の独立プロセスで実行します。
既存の改造版Convexアドオンはインストール・上書きしません。同名モジュールや登録済みプロパティ・operatorを検出したら停止します。

1. Stanford公式 `source/bun_zipper.ply` のSHA-256を照合してSOURCEコレクションへ読み込みます。
2. MIT化した自作MeshRepair 1.23.0でITHappy optimized-cap-v6レシピを実行します（ITHappyモデルは含みません）。
3. Apache-2.0アドオンと同梱V-HACD 4.1で MaxHull=32 / Max Vert Count=32 / Resolution=1000000。
   MaxHullは実行アダプタが `-h 32` を付けます。ほかはアドオン既定値（volume error 10%、depth 10、shrink wrap ON、flood、min edge 2）。
4. 明示的なroot `Stanford_Bunny_Official` と、直下の32個の `UCX_` mesh、`Bunny_VHACD_Convex_32` collectionの一致を検証してUnityへ書き出します。

privateの名前やArmatureツリーを推測して使いません。オリジナルはY-up、単位m。
全objectのworld行列を適用し、表示とConvexに同じ原点調整・4倍拡大・Z反転・winding反転を適用します。
約0.617mのBunnyをtable上に並べ、既存の32凸形状を持つprop登録・katanaヒット・切断・物理経路を使います。
元PLYには底面の5穴があり、修復後は34,834頂点 / 69,664triです。
`generated/repair-report.json`, `generated/convex_report.json`, Unity側の `export-manifest.json` が生成の記録です。

## ライセンス

スコープは [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) を参照。
MeshRepairとレシピはMIT、ConvexアドオンはApache-2.0、V-HACDはBSD-3-Clause。
Bunnyの元データと派生メッシュにはStanford独自の非商用条件が残ります。
この変更でリポジトリ全体や他のコードのライセンスを変更することはありません。
