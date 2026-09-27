# MobPlan専用workerのWindows IL2CPP計測（2026-09-28）

今回のPC・20体・1 Hzの条件では、専用worker 1本で計画更新が間に合った。通常移動のworker実行p95は206 ms、切断を加えた場合は236 ms。最大でも313 msで、計画期限切れ・探索失敗・探索予算切れは0。worker増員を必要とする結果ではない。

一方、切断後の補充タイミングで200〜218 msのフレーム間隔を6回観測した。workerの定常処理が間に合うことと、VRのフレーム予算を満たすことは別であり、補充時のMain処理には追加調査が必要。

## 条件

- Unity 6000.3.22f1、Windows x64、IL2CPP、Release、OptimizeSpeed、Managed Stripping Minimal、Development Player。Profiler接続・Script Debuggingなし。
- Core i7-14700（28論理processor）、RTX 3090、Windows 11 build 22631、D3D11、1280×720、VSync 0、targetFrameRate 90。
- XR自動初期化を診断ビルド中だけ無効化し、実行中の`XRSettings.isDeviceActive=False`を確認。通常描画は有効。起動時のnative OpenXRログにはHMD不在の`XR_ERROR_FORM_FACTOR_UNAVAILABLE`がある。
- `Assets/Licensed/MobPlan/MobPlanCity.unity`、320 clips／66 bones、416 nodes／15,909 edges。既存presetの20体、seed 1、PlayerFlow、計画1 Hz、探索800 ms、commit 2秒、距離LOD有効を維持。全20体を常にnear扱いにした負荷ではない。
- PlanningPool専用1 thread、Normal priority、affinityなし。BackgroundPool／GeometryPoolも通常構成。計測Playerは順番に1つずつ実行し、ビルド終了後に開始。OS全体の負荷やコア割当は固定していない。
- map／Tableロード後10秒をwarmupとして除外。静止60秒、移動120秒、移動＋切断120秒。各条件1回。
- 移動は24秒周期の前進・後退・旋回入力を通常のcircle occupancyへ渡す。切断条件では計画中に約20秒ごとに現在Poseで`TryCut`を呼ぶ。刀Gestureのテストではない。

## 定常運転の結果

単位ms。worker実行は`PrepareExternal`、距離場・goal／flow更新、探索、公開用データ生成を含む壁時計時間。投入待ちと回収待ちは別に計測した。

| 指標 | 静止 60秒 | 移動 120秒 | 移動＋切断 120秒 |
| --- | ---: | ---: | ---: |
| 完了cycle／受理 | 60／60 | 120／120 | 124／118 |
| worker実行 平均 | 70.70 | 93.86 | 113.53 |
| worker実行 p95 | 127.12 | 206.47 | 236.25 |
| worker実行 p99 | 177.21 | 248.79 | 283.61 |
| worker実行 最大 | 177.21 | 261.38 | 313.00 |
| worker実行の期間合計（秒） | 4.24 | 11.26 | 14.08 |
| 投入待ち p95／最大 | 0.44／0.84 | 0.29／0.45 | 0.40／2.57 |
| 回収待ち p95 | 10.06 | 9.89 | 9.87 |
| 要求→回収 p99／最大 | 177.66／177.66 | 255.42／266.36 | 289.02／322.02 |
| 平均CPU使用率（1論理コア=100%） | 1.61% | 3.49% | 2.85% |
| worker実行中の壁時計時間／計測時間 | 7.07% | 9.39% | 11.73% |
| 受理cycle／秒 | 1.000 | 1.000 | 0.983 |
| 受理間隔 最大（秒） | 1.089 | 1.167 | 1.381 |
| 探索失敗／予算切れcycle | 0／0 | 0／0 | 0／0 |
| 公開済み計画の期限外参照を検出したframe | 0 | 0 | 0 |

CPU時間はworker自身で`GetThreadTimes`のuser＋kernel差分を測定した。この環境の値は15.625 ms刻みなので、単一cycleのCPU値よりも期間合計を使う。CPU率は完了cycleのCPU時間合計／計測壁時計時間であり、PC全体の使用率ではない。壁時計時間との差には待ち・スケジューリング等が含まれるが、その内訳は未計測。切断条件のCPU率が低いことから切断が負荷を減らすとは解釈しない。経路・LOD・候補選択が異なる別試行である。

1 Hzは実行頻度であり、毎秒1秒間CPUを使う意味ではない。ただし移動条件では壁時計合計11.26秒に対してOS報告のCPU合計は4.19秒と約2.7倍の差があり、その理由とCPU計測値の妥当性は追加検証を要する。現段階の余裕判断にはCPU率3.5%だけを使わず、壁時計の平均94 ms／回、p95 206 ms、占有率9.4%を基準とする。表のp95／最大は1回の集団計画の値で、60秒／120秒の総合計ではない。

percentileは補間なしの`sorted[ceil((n-1)*p)]`。warmup終了時のゲーム時刻以降に要求された完了cycleを集計している。終了時に処理中のcycleは含めない。フレームはwarmup後の壁時計間隔を集計した。

切断6回すべて受理され、元個体6体のwithdrawal、20→19→20体への補充を確認。計測期間の破棄6件は各切断直後に対応し、探索中の世代が古くなった結果を公開しない動作と整合する。残ったNPCの計画期限切れはない。通常移動の移動量は88.9 m、切断条件は109.0 m（ともにwarmupを含む）。

## worker時間の内訳

| 平均ms | 静止 | 移動 | 移動＋切断 |
| --- | ---: | ---: | ---: |
| `RunCycle`全体 | 59.55 | 37.58 | 39.67 |
| 候補生成カウンタ | 39.47 | 20.43 | 22.28 |
| compatibilityカウンタ | 1.51 | 0.90 | 0.90 |
| assignmentカウンタ | 0.024 | 0.020 | 0.021 |
| worker全体 − `RunCycle` | 11.15 | 56.28 | 73.86 |

候補・compatibility・assignmentは既存plannerのカウンタで、全工程を網羅する排他的な分類ではない。最後の差分には`AcknowledgeExternal`、`PrepareExternal`のgoal field／flow／補充候補、公開データの構築等が入る。個々には分解していない。移動時は探索本体よりその前後の合計が大きく、将来負荷を削る場合は距離場更新等も計測対象にすべき。

## 起動・フレームの別問題

ロードworkerの壁時計時間は静止1,660.8／移動1,620.4／切断1,652.4 ms。これは通常cycleとは分離した。

最初の計画要求では投入待ちが4,002／4,511／3,962 msあり、3試行とも古い結果を1件破棄した。最初の20体を有効化して同期準備する起動期間に対応する。warmupを除いた表からこの現象を隠さず、起動処理の課題として残す。ゲーム時刻は長いframeでclampされるため、ロード後の壁時計10秒とゲーム時刻10秒は一致しない。

定常frame p95は11.30／11.27／11.29 ms。ただし切断条件では6回とも切断の約0.26〜0.65秒後に200.6〜218.1 msの長いframeがあり、そのframeではlive=20、plannerBusy=0だった。別途23.4 msのframeが1回ある。静止・通常移動では22.22 ms超は0回。

補充時には`MobPlanCrowd`がMainで`Instantiate`／`AddActor`を実行し、`SandboxNpcCharacter.Start`が同期的に`Prepare`する。さらに`Prepare`の必要骨走査はbone weightごとに`original.bones`を取得しているため、配列取得の繰り返しも調査対象となる。今回の計測は工程別Mainプロファイルを取っていないので、218 msの原因を特定の関数に断定しない。planner増員でこの停止が解消するとは言えない。

## 再現・証跡

- ビルド入口: `Zantetsu.Sandbox.Editor.MobPlanProfileBuild.Run`
- 診断define: `ZANTETSU_MOBPLAN_PROFILE`。通常ビルドにはCPU計測・自動入力を含めない。
- 手順・引数: [Tools/MobPlan/README.md](../../Tools/MobPlan/README.md)
- 集計: [analyze_profile.py](../../Tools/MobPlan/analyze_profile.py)
- 生ログ・Player: `C:/log/zantetsuken-vr/MobPlanIL2CPP-20260928/`
- `build2.log`: IL2CPPビルド成功、errors 0、warnings 6、1分48.97秒。初回`build.log`は診断フィールドのEditor／Playerレイアウト不一致で失敗し、`NonSerialized`指定後に解消。
- 各`stationary`／`moving`／`cutting`フォルダ: `environment.txt`、`summary.txt`、`cycles.csv`、`frames.csv`、`cuts.csv`、`analysis.json`。3試行ともハーネス判定passed=True、計測開始後のUnity Error／Exception callbackは0。
- ビルド時に発生したURP／PlayerSettingsの自動書換えはビルド前のbyte列へ戻し、XR設定も復元した。全設定を事前snapshotのSHA-256と比較し、一致を確認。既存のユーザー変更は保持。

今回確認したのはこのPCでの専用workerの処理余裕である。実HMD・両眼描画、Quest、より多いNPC、全員near、長時間試験、高いCPU競合条件の保証には使わない。非XR frame値からVR 90 Hz達成を主張しない。
