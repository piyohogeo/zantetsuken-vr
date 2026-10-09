# Quest 3S: NPC10 Slash デモの移植とプロファイル

2026-10-09 JST。専用 worktree `C:/Users/junic/src/zantetsuken-vr-quest3s`、branch `quest3s-appsw`。初回切断・切断前・最終切断後の停止を、ユーザー観察と実機計測で解消確認済み。

## 現在の確認結果

10体の NPC を録画入力で切断する PC デモを Quest 3S へ移植した。頭の動きに追従する視点、90 Hz 表示／AppSW 45 Hz、VP3C-native Vulkan を使用する。512行の入力を実時間で再生し、計測後も世界を保持する。

- 切断後のオブジェクトの影は、Vulkan 用 shadow bias の修正後にユーザーが復帰を確認した。
- native raster pipeline の暖機だけでは初回切断に約404 ms の停止が残った。constant-copy と GPU cull の compute pipeline も起動時に実行した版では、初回切断区間の最大フレーム間隔が25.247 msになった。ユーザーも切断時の停止が消えたと確認した。
- checker/timeline 準備を countdown 前へ移し、刀/wave pipelineも事前準備した最終APKで、ユーザーは切断前と切断時の停止が両方消えたと確認した。新たな表示上の問題は報告されていない。
- 終了CSV保存をworkerへ移し、held logをframeごとに予算付きで放出した最終APKで、ユーザーは最後の切断後の停止も消えたと確認した。CSV外の完了処理を含むraw検証でも、旧90／223 msの停止は解消した。
- 最終APKの計測では36件の切断が provisional／final／commit まで完了し、130 checks が通過した。入力は512/512行、最初の斬撃は10/10体で通過した。CSV全332行の平均45.000008 fps、最大26.928 msで、一部frameが22.222 msの予算を超えた。

AppSW はデプスに基づくカメラ移動の MV を使い、物体の動きの MV を無視する方針。NPC skin／VP描画と、刀/wave の専用shaderでこの方針を実装した。刀/wave は実APKで9 renderer／3 materialの専用shaderロードとXR pass有効を確認した。即切断時の一時 cap に AppSW の描画経路は追加していない。

## どのデモか

元シーンは `Assets/Licensed/MultiNpc/MultiNpcCity.unity`。`MultiNpcSceneBuild` が作る MultiNpcSlash デモで、2列×5体がその場で歩く。モデルは `f_1, f_2, f_6, f_9, f_11, m_1, m_2, m_4, m_5, m_7`。MobPlan の移動する群衆・補充ベンチマークとは別のシナリオ。

背景は tiled seed2 の街で、初期の静的 Quest デモの `tiled_seed14` とは異なる。今回の実行では背景物体数を制限していない。

Quest 専用コピーは `Assets/Licensed/Quest3s/QuestNpcSlash10.unity`、package は `com.junic.zantetsu.questnpcslash`。PC の元シーンを直接変更せず、専用 profile・skin material を作る。

## 再現の前提

- Unity 6000.3.22f1 と Android Build Support（SDK／NDK／OpenJDK）。IL2CPP、ARM64、Vulkan、既存 OpenXR／Meta Quest 設定を使用する。
- Licensed の元シーン、10体のモデル、pose table、切断用 hull などがローカルに存在すること。
- 元の Slash 録画 CSV。既定は `C:\log\zantetsuken-vr\SlashSpan\20260918-153438-normal\input.csv`。別の場所なら `-InputFile` を指定する。
- USB 接続と ADB 認可済みの Quest 3S。ヘッドセットを起動しておき、必要ならシステムの「コントローラーが必要」ダイアログを閉じる。デモ自体はコントローラー操作なしで自動再生する。
- Unity を使用する処理は同じ worktree で1プロセスずつ実行する。

`Assets/Licensed` の生成コピー、StreamingAssets の録画入力、`Build`、`Logs` はローカルの ignored artifacts を含む。以下のリンク先の計測資料はこの worktree に保存されているが、通常の clone だけでは揃わない。

## ビルド・起動

[Build-NpcSlashDemo.ps1](../Tools/Quest3s/Build-NpcSlashDemo.ps1) は native Vulkan plugin をコンパイルし、native shader を bake してから Quest シーンを configure／APK build する。初回は configure と bake を省略しない。

```powershell
.\Tools\Quest3s\Build-NpcSlashDemo.ps1 -Development -InputFile 'C:\log\zantetsuken-vr\SlashSpan\20260918-153438-normal\input.csv'
.\Tools\Quest3s\Run-NpcSlashDemo.ps1 -Apk '.\Build\quest-npc-slash-dev.apk' -StartDelaySeconds 10
```

[Run-NpcSlashDemo.ps1](../Tools/Quest3s/Run-NpcSlashDemo.ps1) は APK を指定した場合にインストールし、package を停止して `files/quest3s-config.json` を更新して起動する。

| 設定 | 既定値／用途 |
| --- | --- |
| `Mode` | `vp3c-native`。比較用に `vp3c` も指定可能 |
| `InputStart` / `InputRows` | 2227 / 512。PC の5 wave baseline に合わせる。最大4096は別の stress 条件 |
| `StartDelaySeconds` | 5。今回の暖機比較は10 |
| AppSW / Profiler / prewarm | ON。比較用に `-AppSwOff` / `-NoProfiler` / `-NoPrewarm` |
| 計測後 | scene を保持。終了する場合は `-QuitWhenDone` |
| native colour preparation | ON。比較用に `-LegacyNativeColour` |
| ADB / serial | `C:\Program Files\RenderDocForMetaQuest\plugins\android\adb.exe` / `340YC10G7D0FZ9`。他端末なら上書き |

起動時の main affinity は Unity 公式の `-platform-android-unitymain-affinity 0x3c` を渡す。Android の読み戻しは `Cpus_allowed_list: 3-5` だった。要求した mask の全 CPU が使用可能だったという記録ではない。

G=1 / B=1 は既存 Android executor の worker 数を使用する。最新 run の Main nice=-20、Geometry requested/observed=-17、Background=-14、Planning=-20。Geometry は Main より3低い優先度、Background は6低い優先度である。`CutWorldProfile` の geometry/background worker count は queue capacity であり、今回変更していない。

## 出力と読み方

デバイスの出力:

```text
/sdcard/Android/data/com.junic.zantetsu.questnpcslash/files/runs/<run>/
/sdcard/Android/data/com.junic.zantetsu.questnpcslash/files/logger/
```

run ごとに `platform.txt`、`quest-config.json`、`run-arguments.txt`、`demo-result.txt`、`comparison/frames.csv`、`profile.raw`、`native-route.txt`、`prewarm.txt`、`prewarm-after-replay.txt` を保存する。`platform.txt` には刀/wave本人のrenderer／unique material数、shader名、motionMode、XRMotionVectors pass index／enable状態も保存し、9 renderer／3 materialが実APKで読み込まれたことを確認できる。logger の JSONL は別フォルダーなので、採取時には同じ run の `logger/` へ併せて保存する。logger に古い session が残る場合は `npc_run_begin/end` の run directory で対応づける。

集計例:

```powershell
python .\Tools\Quest3s\Summarize-NpcSlashDemo.py .\Logs\quest-npc\<run> --logcat .\Logs\quest-npc\<run>\logcat.txt --logger-directory .\Logs\quest-npc\<run>\logger --output .\Logs\quest-npc\<run>\summary.json
```

[Summarize-NpcSlashDemo.py](../Tools/Quest3s/Summarize-NpcSlashDemo.py) は失敗 run も出力し、CSV の全行を集計する。percentile は nearest rank。FrameTiming の未取得 GPU／render 値は unavailable として扱い、処理時間0と解釈しない。logger enqueue の `Accepted` だけではファイル永続化を証明しないため、保存された JSONL の `npc_run_end` を確認する。

raw Profiler は NPC ready 後、暖機より前から収録する。`platform.txt` の `ready / warm-begin / warm-end / check-ready / replay-begin` に application frame、unscaled/realtime、UTC を保存する。raw の frame 番号と CSV の application frame 番号は異なる。暖機は CSV の replay 計測区間外にある。

## 移植に追加した実装

| 領域 | 主な変更 |
| --- | --- |
| [QuestNpcSlashBuild](../Assets/Zantetsu/Editor/Quest/QuestNpcSlashBuild.cs) | NPC10 専用シーン／profile／material コピー、録画 CSV の StreamingAssets への配置。GPU geometry 初期容量をPCの512 MiBから32 MiBへ縮小し、通常の拡張は維持 |
| [QuestNpcSlashDemo](../Assets/Zantetsu/Runtime/Quest/QuestNpcSlashDemo.cs) | Quest限定 bootstrap、入力抽出、head tracking、AppSW／native route、Profiler・phase・結果保存、暖機完了待ち、keep-open |
| [SandboxPropSlashPlayerCheck](../Assets/Zantetsu/Runtime/Sandbox/SandboxPropSlashPlayerCheck.cs) | `StartPrepared` による明示起動。引数 override は prepared 起動時だけ使用し、PC の Environment argv による既定起動は維持。測定終了後の metrics／LateUpdate を停止 |
| [SandboxSlashPoseRecorder](../Assets/Zantetsu/Runtime/Sandbox/SandboxSlashPoseRecorder.cs) | `ReplayInRealtime`。各 Update で録画時刻に到達した全 sample を入力し、90 Hz の CSV を45 Hzで一行ずつ遅く再生しない。PC既定は一行／frameのまま |
| Check の realtime 観測 | `RealtimeSampleEvaluated` で各 sample の hit／row index／sweep count を次の Evaluate に上書きされる前に回収。最後の観測 queue が空になるまで完了を待つ |
| [Parts](../Assets/Zantetsu/Runtime/Sandbox/SandboxPropSlashPlayerCheck.Parts.cs) | keep-open は1 iterationで世界／poseを保持し、world release 未実行であることを run result に明記 |
| [ReportSave](../Assets/Zantetsu/Runtime/Sandbox/SandboxPropSlashPlayerCheck.ReportSave.cs) | Quest retained NPC runのtimeline snapshot→worker CSV保存、main logの予算付きdrain、保存・close・drainのguarded完了。PCは従来同期 |
| [DevelopmentLoggerLifecycle](../Assets/Zantetsu/Runtime/Core/DevelopmentLoggerLifecycle.cs) | Androidでは APK内 metadata を UnityWebRequest で取得・厳密検証し、`persistentDataPath/logger` へ出力。PC の同期 metadata 読み出し／既存ログ先は維持 |
| camera-only skin MV | NPC専用 skin material に `VP_CAMERA_ONLY_ASW` を有効化し、物体移動分を出さない |
| [QuestCameraOnlyLit](../Assets/Zantetsu/Runtime/Quest/Shaders/QuestCameraOnlyLit.shader) / [Unlit](../Assets/Zantetsu/Runtime/Quest/Shaders/QuestCameraOnlyUnlit.shader) / [shared motion](../Assets/Zantetsu/Runtime/Quest/Shaders/QuestCameraOnlyMotion.hlsl) | 刀5 renderer／wave4 renderer、3 materialをQuest専用cloneへ置換。URPの元PropertiesとXR以外のpassを保ち、XRMotionをcurrent world positionとcurrent／previous camera VPで出す |
| [VpNativeDrawRoute](../Assets/Zantetsu/Runtime/RenderingUrp/VpNativeDrawRoute.cs) / [Vulkan plugin](../Native/ZantetsuVpNative/ZantetsuVpVulkan.cpp) | native graphics PSO の warm-only event と結果確認、backend別 shadow bias、constant-copy／empty cull compute の初回実行 |
| [QuestNpcUnityPrewarm](../Assets/Zantetsu/Runtime/Quest/QuestNpcUnityPrewarm.cs) | 既存 opaque stencil init／volume／cap material を3 frame描画して Unity PSO を準備。volume/cap は degenerate geometry。使用後4 frame以上と GPU fence 完了を待って buffer を解放 |

初回 APK では Android の `QualitySettings.skinWeights=TwoBones` により character cut preparation が失敗した。NPC の authored four weights を保持するため、builder と runtime で対象 `SkinnedMeshRenderer.quality=Bone4` を指定し、unit-scale renderer も同設定を継承する。global quality は変更していない。

Quest は `supportsGraphicsFence=True` でも `supportsAsyncCompute=False` だった。最初の AsyncQueueSynchronisation fence query は例外になったため、helper は CPUSynchronisation を使用する。query／retirement の未知例外は記録し、暖機失敗時は replay に進まない。使用中 buffer を早期解放しない。

compute 暖機では空の command batch に cull kernel を1 group実行する。count guardにより物体は処理しない。constant-copy kernel は実 pipeline の offset table／globals と pass の scratch buffer を使用し、実描画が読む値は後で上書きする。native graphics 暖機は実際の render pass／subpass 状態で `vkCreateGraphicsPipelines` を行い、切断や本描画は実行しない。

通常物体は NoClip GPU indirect、少数の即切断物体は CPU direct Clip 経路を維持する。暖機のために cap の AppSW／shadow geometry を追加していない。刀/wave の実 `MeshRenderer` 本人参照を facade で取得し、同じ vertex stream layout・material・render state の退化 geometry clone を3 frame描画して、その通常カラー／shadow／XRMotion pipeline も事前準備する。元の刀/wave の可視状態は変えない。

## 影の修正と証拠

D3D12 用に較正した depth bias=256 が Vulkan にも渡されていた。native caster の既定を Vulkan=1、D3D12=256 とし、slope=2.5を維持した。Quest の reversed Z では signed bias=-1/-2.5。URP ShadowUtils の global bias `(1, 2.5)` と合わせ、shadow slice／viewport／GPU projection の既存経路を使用する。

ユーザーは修正版で切断後の影の復帰を確認した。d5822f run の [native-route.txt](../Logs/quest-npc/20261009T001755Z-npc10-vp3c-native-d5822f/native-route.txt) は caster runs taken=341、caster issues=439、last splits=1、not drawn=0、refused=0、pipeline failures=0、caster bias=-1/-2.5 を記録する。ユーザー確認と描画経路の成功ログによる確認であり、最新 APK の shadow depth の RenderDoc ピクセル検証は実施していない。

## 初回切断の比較

同じ Quest 3S、Development IL2CPP、Vulkan、head tracking、AppSW ON、input start2227／512行。MSAA requested=4、暖機ありの start delay=10秒。Profiler／checks／metrics 有効。

| run | 暖機 | CSV行数 | delta平均 / p95 / 最大 (ms) | checks / 切断 |
| --- | --- | ---: | ---: | --- |
| [e415d6 summary](../Logs/quest-npc/20261008T233542Z-npc10-vp3c-native-e415d6/summary.json) | 無し | 289 | 25.375 / 24.137 / 712.520 | 117 / 32 |
| [8380af summary](../Logs/quest-npc/20261009T000538Z-npc10-vp3c-native-8380af/summary.json) | native graphics + Unity | 303 | 24.202 / 25.208 / 403.650 | 117 / 32 |
| [d5822f summary](../Logs/quest-npc/20261009T001755Z-npc10-vp3c-native-d5822f/summary.json) | 上記 + compute | 322 | 22.844 / 22.513 / 133.938 | 141 / 40 |

前2 run の最大値は初回切断の停止である。d5822f run の全体最大133.938 msは再生開始時の checker preparation（app frame819）で、初回切断区間の値ではない。d5822f の初回切断区間 app883–898 の最大は **25.247 ms**。したがって初回切断停止は約713 → 404 → 25.25 msと短縮した。

raw RenderThread の比較:

| run / raw frame | RenderLoop (ms) | VP Native Casters (ms) | 解釈 |
| --- | ---: | ---: | --- |
| e415d6 / 65 | 671.887 | 381.160 | 初回 graphics／compute 使用時の停止 |
| 8380af / 522 | 397.986 | 339.976 | graphics は warm cache hit、compute の初回使用が残った |
| d5822f / 524（app883） | 26.769 | 1.816 | 最初の accepted cut。Main 26.705 ms |

d5822f は native warm16件完了、constant-copy1／cull1 dispatch完了。本 replay で使用した7 pipeline variantは全て warm cache hit、`drawMissesAfterWarm=0`。未使用の9 variantも準備したが、その準備だけで replay の描画網羅性を主張しない。

起動時の raw frame1（app360）の Render は940.765 ms、Constant Copy Warm は332.939 ms、GPU Cull Warm は38.007 ms。`warm-begin`→`warm-end` の realtime 経過は約1.103秒。初回 GPU 準備コストを消したという意味ではなく、replay 前へ移した。

d5822f run の全322行平均は **43.776 fps**。33.333 msを超えたのは3行（app819=133.938 ms、app855=46.08 ms、app1136=88.48 ms）。後続切断56行は delta平均22.226／最大23.300 ms、最終 commit app1071 後63行は平均22.222／最大22.980 ms。Check集計・終了ログ等の停止は切断計算と区別するが、全体集計から除外しない。

d5822f の XR coarse GPU は平均10.905／p95・最大13.336 ms。ただし値は9種類・8回更新で、毎frame独立した GPU計測ではない。初期 run とd5822f run の最終 fragment／convex は42/309 → 50/354へ変わっている。停止の有無が実時間再生中の相互作用に影響するため、cut数や CPU/GPU平均を同一仕事量の速度比較として扱わない。

compute暖機版の再実行（suffix `9fb076`）は code0で完了。ユーザーは切断時停止の解消を確認し、切断前の短い停止を報告した。この報告を受けた追加調査では、replay-begin の app821 に約133 msの停止があり、Walk.Start 約40 ms（self約30.7 ms）と最初の Check.Cuts 約80.862 ms（self約77.741 ms）が重なっていた。後者は10体の BakeMesh と頂点grid比較を行う MultiCheckStart。刀/wave の最初の Unity opaque／XRMotion pipeline 準備にも約24.5 msが残っていた。

追加修正では Quest realtime+multiNpc に限り、timeline recorder／row pool、hit recorder、MultiBegin の記録準備、開始geometry検査を countdown 前へ移した。各NPCの authored source offset の開始poseを適用して検査し、`check-ready` phaseを保存した後に指定秒数待つ。countdown中は `_timing=false / _timelineBegun=false / _replaying=false` のままで、CSVに起動準備の行を加えず、入力も進めない。実際の開始時に従来どおり replay clock と全10体の Pose.Restart を設定する。PC／MobPlan／building／cityWalk の準備順序は維持する。

刀/wave pipeline の暖機も追加済み。また、既存 URP Lit/Unlit の XRMotion は `MotionVectorGenerationMode.Camera` だけでは camera-only を保証できなかった。Quest 専用 material clone に、stock の colour/shadow/depth と current world position × current/previous camera VP の独自 XRMotion pass を持つ shader を追加し、刀/wave も物体MVを出さない実装へ揃える。Unity importで UsePass方式が失敗したため、最終版はインストール済み URP Lit/Unlit の全shaderを元にした。Shader名とXR passだけを変え、PropertiesとXR以外の8/6 passは元sourceと一致することを確認した。Unity Companion Licenseの帰属headerを保持する。`motionVectorGenerationMode=Object` は現在geometryを低解像度depthへ登録するための指定であり、独自XR shader自体は previous object matrix／object history を使わない。Unity configure は成功し、刀5 Lit renderer／wave4 Unlit renderer、3 materialとshader import正常を確認した。ソースcommit41a317d2のAPKをbuildし、run `20261009T004820Z-npc10-vp3c-native-898b55` は code0で完了した。CSV／機能結果とユーザー観察で、切断前／切断時の停止解消を確認した。終了時に残った停止は、後述の最終版で解消した。ユーザー観察とCSVでは追加修正の効果を確認した。上表は追加修正前3runの値であり、898b55の値は以下へ別記する。

### 追加修正版: 898b55

実装commit `41a317d2` の run `20261009T004820Z-npc10-vp3c-native-898b55`。ユーザーは切断前／切断時の停止解消を確認したが、最後の切断後に短い停止を報告した。

| 区間 | CSV行数 | delta平均／最大 (ms) | 33.333 ms超 |
| --- | ---: | ---: | ---: |
| CSV全行（observeを含む） | 328 | 22.425／90.403 | 1行 |
| replay phase | 323 | 約22.22／26.805 | 0行 |

CSV全行の平均44.592 fps、p95=22.641 ms、p99=23.325 ms。replay区間は約45 fps。従来 fed83 付近の切断前区間は最大22.222435 msとなり、旧runの44.796／46.083 msと再生開始時133 msの停止は解消した。

CSVの最大90.403 msは observe phaseのapp1134で、その前のapp1133/raw776に Main 88.040 ms／4.902 MB allocation を記録した。Walk.Start coroutineは70.491 ms（self59.598 ms）、Renderは88.314 msのうち待機70.734 ms。graph処理は1.568 msで、shader／pipeline作成による停止ではなかった。

さらにCSV計測の停止後にも、app1138/raw781で Main 222.916 ms／Walk.Start 217.044 ms（self63.115 ms）を確認した。最終CSV書き出しとheld logのReleaseLog→多数のLogStringToConsoleが重なっている。**CSV全行集計だけでは、全ての完了処理の停止を網羅しない。** 90 msと約223 msの両方が終了処理修正の対象となった。898b55のCSVだけを見て終了まで45 fpsを維持したとは主張しない。

終了処理の追加修正を実装した。Questのretained realtime NPC runに限り、main threadでtimelineのmanaged値snapshotを取り、CSV整形／ファイル保存を有限のworkerへ移す。Unity sampling／ProfilerRecorderの所有はmainに残し、mutableな末尾rowはcopyしてからworkerに渡す。held logはmainで1 frame最大16行／約2 msの予算付きdrainへ変える。worker完了とguarded close／log drain後にDoneとし、保存・close失敗は既存Complete guardに集約する。ProfilerはDone後に停止するためCSV外の完了処理もrawへ残る。PCの同期保存・終了、CSV末尾の境界は維持する。report saveを含むfocused EditMode testsは7/7通過し、独立レビューも完了した。sourcecommit `6ed61141047a838c0af9ea117f3a53ae32fe9330` に保存した（5 files）。最終APKのfb989f runで性能改善とユーザー観察を確認した。

43切断全てP/F/Commit、pending0、151 checks、firstSlash10/10、512入力を確認した。保存JSONLはCheckHits 43 hits／43 ops、CheckFrames 323 frames、queueFull0／refused0、`npc_run_end` app1140で永続化を確認した。151件には開始pose適用を確認する追加1件と、切断数増加に対応するチェックを含む。刀5 Lit／wave4 Unlitの実renderer計9、unique material3、全XR pass有効、Unity暖機3 frameを [platform.txt](../Logs/quest-npc/20261009T004820Z-npc10-vp3c-native-898b55/platform.txt)／[prewarm.txt](../Logs/quest-npc/20261009T004820Z-npc10-vp3c-native-898b55/prewarm.txt) で確認した。`check-ready` はapp367、replay前に保存されている。

XR coarse GPUは平均12.497／p95・最大13.169 ms。仕事量・camera pose・GPU計測更新頻度の差があるため、先行runの平均値との差を同一仕事量の速度差として解釈しない。[全行summary](../Logs/quest-npc/20261009T004820Z-npc10-vp3c-native-898b55/summary.json)。

### 最終版: fb989f

sourcecommit `6ed61141047a838c0af9ea117f3a53ae32fe9330`、run `20261009T010513Z-npc10-vp3c-native-fb989f`。ユーザーは最後の切断後の停止も解消したと確認した。報告された3か所の停止は全て解消した。

| 項目 | 最終版の結果 |
| --- | --- |
| 機能 | code0、130 checks、512/512入力、firstSlash10/10 |
| 切断 | 36 accepted、36 provisional/final/commit、pending0。commitは1 frame32件／2 frame4件 |
| CSV全行 | 332行、平均45.000008 fps。delta平均22.222218／p95 22.375673／最大26.928125 ms、33.333 ms超0行 |
| 切断前／初回切断 | 旧fed83区間最大22.222443 ms。初回切断＋3 frame区間最大22.501934 ms |
| raw: cuts complete→Done | app1134–1234の101 captured frames（末尾1234はProfiler stop途中）。100 complete interframe intervalsの平均22.227213／p95 23.444638／最大24.419169 ms、33.333 ms超0 |
| raw: Main／Render | 完了区間の最大24.4181／24.4180 ms。待機を含む |
| raw: summary／final close | summary app1142=13.9759 ms（self12.8838）、final close app1232=6.2840 ms（self2.9626） |
| raw: log drain | app1146–1231の86 calls、最大2.96672 ms。単独LogStringToConsole最大0.728854 ms |
| 暖機 | Unity3 frame／9 renderer／3 unique materials、native16、constants1／cull1。使用7 pipelineはwarm後cache miss0 |
| JSONL | 36 cut全P/F/Cとcomplete/endを保存、`npc_run_end` app1234を確認 |

CSV末尾後のapp1143–1234もrawへ残して確認した。CSV区間を短くして終了処理を隠した結果ではない。app1234のMain0.679846／Render0.269896 msはProfiler停止途中の部分記録なので、完全なframe時間として比較しない。

CSVのFrameTiming Main平均7.418869／最大24.499743 msは待機を含み、present wait平均0.009291 ms。XR coarse GPUは平均12.157762／最大12.546309 msだが、9種類・8回更新と疎であり、同じCSV行と同期したGPU計測ではない。ftRender／ftGPUの0はunavailable。GCは1回、allocated 8.569612 MB。前版は43 cut、最終版は36 cutなので、GPU平均の差だけを最適化効果と断定しない。

[最終summary](../Logs/quest-npc/20261009T010513Z-npc10-vp3c-native-fb989f/summary.json)、[phase／shader metadata](../Logs/quest-npc/20261009T010513Z-npc10-vp3c-native-fb989f/platform.txt)、[native route](../Logs/quest-npc/20261009T010513Z-npc10-vp3c-native-fb989f/native-route.txt)、[warm cache](../Logs/quest-npc/20261009T010513Z-npc10-vp3c-native-fb989f/prewarm-after-replay.txt)。

### 保存した profile

- 初期版: [profile.raw](../Logs/quest-npc/20261008T233542Z-npc10-vp3c-native-e415d6/profile.raw)、[frames.csv](../Logs/quest-npc/20261008T233542Z-npc10-vp3c-native-e415d6/comparison/frames.csv)、[raw samples](../Logs/quest-npc/20261008T233542Z-npc10-vp3c-native-e415d6/raw-analysis/raw-samples.csv)。
- graphics 暖機版: [profile.raw](../Logs/quest-npc/20261009T000538Z-npc10-vp3c-native-8380af/profile.raw)、[frames.csv](../Logs/quest-npc/20261009T000538Z-npc10-vp3c-native-8380af/comparison/frames.csv)、[prewarm-after-replay](../Logs/quest-npc/20261009T000538Z-npc10-vp3c-native-8380af/prewarm-after-replay.txt)。
- compute 暖機版: [profile.raw](../Logs/quest-npc/20261009T001755Z-npc10-vp3c-native-d5822f/profile.raw)、[frames.csv](../Logs/quest-npc/20261009T001755Z-npc10-vp3c-native-d5822f/comparison/frames.csv)、[platform phases](../Logs/quest-npc/20261009T001755Z-npc10-vp3c-native-d5822f/platform.txt)、[prewarm](../Logs/quest-npc/20261009T001755Z-npc10-vp3c-native-d5822f/prewarm.txt)、[raw samples](../Logs/quest-npc/20261009T001755Z-npc10-vp3c-native-d5822f/raw-analysis/raw-samples.csv)、[logcat](../Logs/quest-npc/20261009T001755Z-npc10-vp3c-native-d5822f/logcat.txt)。

- 追加修正版: [profile.raw](../Logs/quest-npc/20261009T004820Z-npc10-vp3c-native-898b55/profile.raw)、[frames.csv](../Logs/quest-npc/20261009T004820Z-npc10-vp3c-native-898b55/comparison/frames.csv)、[phase／shader metadata](../Logs/quest-npc/20261009T004820Z-npc10-vp3c-native-898b55/platform.txt)、[raw samples](../Logs/quest-npc/20261009T004820Z-npc10-vp3c-native-898b55/raw-analysis/raw-samples.csv)、[logcat](../Logs/quest-npc/20261009T004820Z-npc10-vp3c-native-898b55/logcat.txt)。

- 最終版: [profile.raw](../Logs/quest-npc/20261009T010513Z-npc10-vp3c-native-fb989f/profile.raw)、[frames.csv](../Logs/quest-npc/20261009T010513Z-npc10-vp3c-native-fb989f/comparison/frames.csv)、[raw samples](../Logs/quest-npc/20261009T010513Z-npc10-vp3c-native-fb989f/raw-analysis/raw-samples.csv)、[logcat](../Logs/quest-npc/20261009T010513Z-npc10-vp3c-native-fb989f/logcat.txt)。

## 検証

| 保存結果 | 対象 | 結果 |
| --- | --- | --- |
| [quest-npc-editmode.xml](../Logs/quest-npc-editmode.xml) | SandboxRightHandKatana 172、PropSlashParts 5。realtime時刻／複数sample／通知、PC既定、保持終了等 | 177/177 passed |
| [quest-npc-logger-tests.xml](../Logs/quest-npc-logger-tests.xml) | metadata strict validation と既存 logger | 9/9 passed |
| [quest-npc-warm-editmode.xml](../Logs/quest-npc-warm-editmode.xml) | VpNativeDraw 24、native pointer facts 3 | 27/27 passed |
| [quest-npc-report-tests.xml](../Logs/quest-npc-report-tests.xml) | PropSlashParts 5、report save 2。終了保存の追加修正 | 7/7 passed、failed0／skipped0 |
| [quest-npc-warm-contract.log](../Logs/quest-npc-warm-contract.log) | Android ABI／parser／malformed input／direct range／group overlap／paired preparation lifetime／warm event | passed |
| compute 暖機版の Android 実行 | native16 + compute1/1、7 actual variants cache miss0、512入力、40 P/F/Commit、141 checks、保存JSONL | passed |
| 最終版の Android 実行／raw／ユーザー観察 | 36 P/F/Commit、130 checks、CSV332行とCSV外完了100 intervals、保存JSONL、報告3か所の停止解消 | passed |

追加修正のUnity configureでC#／shader compileを確認し、9 renderer（5 Lit／4 Unlit）・3 materialを確認した。compute 暖機を追加した後の広範な EditMode 再実行は未実施。上記 XML はその前の実行履歴であり、最新compute変更を全て単体テストで検証したという意味ではない。実機 startup／replay の profile と完了ログを確認した。

## ソース・APKの識別限界

初期3runと再実行9fb076のベース HEAD は `3f2308fb5a271920dc05b12b1a69c1e2dc9ed3db`。これらのAPKはNPC移植／暖機／logger等の dirty／untracked 変更を含み、HEADだけでは全ソースを再現できない。これらのloggerのcommit prefixはベースHEADを示し、dirty source内容を識別しない。

追加修正版の実装は `41a317d2a13593a187689a857d7224927bd9a161` にcommitした（36 files、native SO／shader込み）。898b55のAPKはこのcommitからbuildした。終了停止の追加修正は `6ed61141047a838c0af9ea117f3a53ae32fe9330` に別commitし、最終APKをこのcommitからbuild・実機検証した。この文書とignoredの生成Licensed scene／録画入力／baked・計測artifactsは別途保存対象。run末尾 `e415d6 / 8380af / d5822f / 9fb076 / 898b55 / fb989f` は run識別用 suffixで、Git commitではない。

| APK | Build GUID | SHA256 |
| --- | --- | --- |
| compute 暖機版（追加修正前、d5822fと再実行） | `5ea24ad3d8d24cd7810753f9d623a27f` | `6F7FCBA467AE98F216C787B4FCFAA318E0D0B46F6B655CA187A469F96A614C34` |
| [checker準備移動＋刀/wave camera-only・暖機 APK](../Build/quest-npc-slash-start-warm-cf3abd6f.apk)（41a317d2、898b55） | `c67edcebd0ff4c489ad83c0907b769a7` | `CF3ABD6F5EEE44149BFA1E8D190D46EA5C275FF0801983D768FDA5EFE2C16447` |
| [終了CSV worker保存／log分割 APK](../Build/quest-npc-slash-report-fix-b42b1e34.apk)（6ed61141、fb989f） | `6c5845941bbc4e56a4d226840e97a6ef` | `B42B1E3413C5DB4C700278D50453AFDA0AE3A32F4C0F9D6C4D4CCD1DA638F560` |

最終APKは `Build/quest-npc-slash-report-fix-b42b1e34.apk` に保存した（150,887,753 bytes、上記SHA256／Build GUID）。

898b55のAPKは `Build/quest-npc-slash-start-warm-cf3abd6f.apk` に保存した（128,252,099 bytes、上記SHA256）。終了停止を修正する前の実装・計測として保持する。

初期版の Build GUID は `7f44e43e0f524b32a11bf2c35d18e505`、graphics暖機版は `7c29a403cd89425fa6df46d1edd39700`。`Build/quest-npc-slash-dev.apk` は後のビルドで上書きされるため、その現在の hash を過去 runへ割り当てない。再現保存には source差分、generated/baked assets、APK、Build GUID、SHA256、run config、profile を同時に残す必要がある。

## 遠方信号機の二重像とAppSpace修正（2026-10-09）

頭を動かすとfar clip付近の信号機が数pxの二重像になるという報告を調査した。アプリ停止中は頭を動かしてもぶれないというユーザー観察も得た。実機cameraはnear=0.01 m／far=100 m、colour=1680×1760×2／MSAA4、MV/depth=420×440×2／MSAA1だった。

### GPU入力の確認

RenderDocで一つの実frame1118を取得した。capture APIを二度呼んだが同一frame・同一ファイルを返したため、独立した二つのcaptureとは数えない。

- stock `XRDepthMotionPass`（EID1028）が背景MVを描いた後、native motion passはcolour/depthをLoadした。後続native描画による背景MVの消去は見られなかった。
- 背景pixel (160,50) のfloat MVは `[0.0652466,-0.0119171,0,0]`、(315,220) は `[0.107727,0.0281677,0,0]`。native motion後（EID1153）も同じ値だった。alpha=0でも背景RGBのMVは描かれている。
- captured native current/previous VPで計算した背景XYのMVは、取得float値と約0.04 colour pixel以内で一致した。nativeに渡されたprevious/current VPのbyte列がUnity側GPU定数bufferにも連続して存在すること、native `_SpaceWarpNDCModifier=1.0` を確認した。
- これは背景の二点とGPU定数の確認である。問題の信号機の正確なpixelやcompositor合成出力を直接計測したものではない。capture自体が描画周期へ影響するため、このframeから通常時の頭の角速度やframe遅延を推定しない。

距離によるMV差の目安は `f×Δx×(1/z_object−1/z_background)`。仮にf=700 px、横移動2 cm、物体100 m／背景無限遠でも0.14 px程度となる。一方、回転30°/sに11.11 msの姿勢差がある場合は中央付近で約4.1 pxになり得る。これは大きさの例であり、実機で11.11 msの遅延を測ったという意味ではない。

[GPU captureと解析出力](../Logs/appsw-signal-audit/)、[colour画像](../Logs/appsw-signal-audit/colour.png)。`motion.exr` はexport APIがPNGを書いたものであり、float解析には使っていない。float値はpixel APIから取得した。

### 原因と同じAPKによるA/B

従来はTrackedPoseDriverで更新されたMainCameraのworld position／rotationを `SetAppSpacePosition/Rotation` へ渡していた。[XR_FB_space_warp仕様](https://github.com/KhronosGroup/OpenXR-Docs/blob/main/specification/sources/chapters/extensions/fb/fb_space_warp.adoc)のappSpaceDeltaPoseはアプリが適用した空間変換を表す。インストール済みUnity OpenXRのAndroid arm64 binaryでは、`MetaSetAppSpacePosition/Rotation` が与えられたposeを保持し、`OculusSpaceWarp::ExtendView` が前回との差分をspace-warp layerへ書く経路を確認した。この経路で物理HMD poseを差し引く処理は見られなかった。

比較APKは [quest-npc-appspace-diagnostic-dev.apk](../Build/quest-npc-appspace-diagnostic-dev.apk)、Build GUID `f32e1527d2574432be362169e8ad3532`、SHA256 `6BBEE3188235C452BDECE966E1FE9EC6849099EA1CE2E508907F57BF84A94E81`。同じAPKでAppSpace poseの選択だけを変えた。

| AppSpace入力 | 姿勢ログの例（5秒区間の最大frame差分） | ユーザー観察 |
| --- | --- | --- |
| cameraの親 `Camera Offset`（tracking-origin） | camera 2.5589°、AppSpaceへ送る姿勢0° | 二重像が消えた |
| 従来のMainCamera（camera）へ戻す | camera 3.5990°、AppSpaceへ送る姿勢3.5990° | 二重像が再発した |

両方の安定区間でVrApi `FPS=45–46/90`、`ASW=90–91,Type=App`、`Stale=0` を確認した。暖機・起動区間にはStaleがある。頭の動きをAppSpaceのアプリ移動へ含める入力が、今回の二重像の原因と判断した。通常HMD trackingとcamera-only MVは維持し、AppSpaceにはcameraの親のworld poseを渡す。親はtracking spaceをシーンへ配置する変換なので、rigの人工移動があればその移動は含む。

[tracking-originログ](../Logs/appsw-signal-audit/tracking-origin-logcat.txt)、[従来cameraログ](../Logs/appsw-signal-audit/camera-legacy-logcat.txt)、[実機platform](../Logs/appsw-signal-audit/tracking-origin-platform.txt)、[比較config](../Logs/appsw-signal-audit/tracking-origin-config.json)。

NPCのconfigと `Run-NpcSlashDemo.ps1` の既定値を `tracking-origin` へ変更した。`-AppSpacePoseMode camera` は再現比較用として残す。同じ入力をしていた `QuestStaticDemo` もcameraの親へ変更した。static runnerはrigを移動するため、path移動も親のworld poseに含まれる。静的デモの今回の変更はAndroid compileまでの確認であり、静的シーンでの新たな実機A/Bは未実施。

最終 [quest-npc-appspace-fixed-dev.apk](../Build/quest-npc-appspace-fixed-dev.apk) をbuild・installし、launcherの既定tracking-originで起動した。Build GUID `2b55c78615c64066bbbea191cb891f32`、SHA256 `7A26F711CE6C82BE661CA15C1E8828A42F939BAEF5A7C327181DA5524E746EEF`。Android build errors=0、run `20261009T015722Z-npc10-vp3c-native-abf873` はcode0・sceneRetained=Trueで完了した。終了後の安定区間でもFPS45–46/90、ASW90–91 TypeApp、Stale0を確認した。[最終platform](../Logs/appsw-signal-audit/fixed-platform.txt)、[完了結果](../Logs/appsw-signal-audit/fixed-demo-result.txt)、[最終ログ](../Logs/appsw-signal-audit/fixed-logcat.txt)。

## 関連記録

- [Quest静的 AppSW デモ](quest3s-static-appsw.md)
- [VP3C-native Vulkan](quest3s-vp3c-native-vulkan.md)
- [通常物体／Clip direct経路の分離](quest3s-vulkan-clip-route-split.md)
- [Vulkanカラー準備分離](quest3s-vulkan-colour-pass-preparation.md)

## AppSWの距離制限実験

[AppSWの物体距離制限（追加制限なし／60m／30m）の比較](quest3s-appsw-far-distance.md)を参照。カラーfar100m、PS変更なしの物体単位カリングで比較した。
