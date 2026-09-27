# MobPlan取り込み実装記録（2026-09-28）

実装を本作業ツリーへ取り込み、privateデータから20体の都市シーンを生成した。commit／pushは行っていない。既存の別作業の変更は保持している。

## 実装

- `Runtime/Core/MobPlan`: probeの数値探索、停止tail、候補選択、固定polygon map、goal field、PlayerFlow。公開計画は所有したsegmentコピーを持ち、明示時刻を任意順に参照できる。範囲外の未来参照は拒否する。
- `SharedWorkDispatcher`へ`MobPlanning`／`PlanningPool`を追加。専用1 thread、Normal、affinityなし、保持容量2。urgent予約に加え、通常の容量構成では計画待機1枠を投機・maintenanceから保護する。旧3実行先の利用者との互換を維持する。
- `MobPlanCrowd`はMainで投入・回収・公開とRoot適用を行う。探索、距離場、Flow更新、初期map warmupは専用worker。集団ごとに1件だけ進行し、世代変更／期限切れの結果は集団単位で捨てる。次の探索は実際に公開されたbaselineから始める。計算中にMainで同期探索しない。
- `MobPoseBank`はC0後の66本Tableを共有し、yaw派生は骨配列を共用する。Rootはprobe observerと同じ配置式を使用。`PoseTablePlayer`へ明示Source Timeを渡すため、Clip切替で再読込・Transform検索を行わない。
- 骨LODは全Clipの位置長上限と骨chainから保守的な範囲をロード時に作り、Rig/bank間で共有する。今回の実Rigでは適用本数66/36/34/32。単一Clipの範囲を別Clipへ流用しない。
- PlayerはNPCと同じ静的mapを共有し、円の全XZ→Xのみ→Zのみ→位置保持、独立旋回。HMD追跡を変更しない。専用入力は1.49 m/s、旋回120 deg/s、後退係数0.5。XR左stick／gamepad左stick／矢印・WASDを使用する。
- 切断のwithdrawalを見て計画から退役させ、後着結果を失効させる。切断元は復活させず、未使用templateから別個体を補充する。未切断NPCのrecycle／補充は現在HMD方位と固定map遮蔽で公開時にも再検査する。
- kinematic Rigidbodyへ速度を書き込まず、計画Rootの並進／角速度とCOM offsetから切断時の継承速度を渡す。

## データと再現

private package: `C:/Users/junic/src/zantetsuken-assets-private/MobPlan/ITHappy_f_1`。`.bytes`はパッケージ内`.gitattributes`でLFS対象。mainの配置先はgitignore対象の`Assets/Licensed/MobPlan`。SHA-256検証付きインストーラは [Tools/MobPlan](../../Tools/MobPlan/README.md) にある。

生成シーン: `Assets/Licensed/MobPlan/MobPlanCity.unity`。本隊cityの座標移動`(390, 0.1, 150)`をmapとの接続に使用する。既存のCasual rig／physics intake／都市assetを前提に、Editorメニューから再生成できる。

| 項目 | 結果 |
| --- | --- |
| planning graph | 416 nodes、15,909 edges、wait-capable 2 |
| C0 run | `20260927-112952-ITHappy_f_1-inertial-rootmotion-yaw-30_-20_-10_10_20_30-v0.3-locomotion` |
| Table | 320 clips、21,334 samples、643→66 bones |
| Tableファイル合計 | 414,867,220→42,747,116 bytes（約396→40.8 MiB） |
| Root検証 | 全416 nodes、14,667点でplanar datasetとの位置差最大0 m |
| dataset SHA-256 | `61c8def1b9605ed6ebbbbfa97b5be63484e6e811ccb38d511c7f5774940bed90` |
| map SHA-256 | `3d8fb4797e642adf37f79bdfc9dbdc350af321600f23b70b315a546ace959072` |

必要骨は実際の非zero skin weight、renderer/root、19 hull骨、祖先closureから抽出し、66本であることを再確認した。全320 Tableの必要sampleをbyteコピーし、root／timing／contactを保持する。parent／humanoid mappingを再採番し、同じ66本への再縮小がbyte一致することも確認している。C0最適化・retargetの再実行はしていない。

## 確認結果

- Unity 6000.3.22f1のコンパイル、生成シーンでの実行、privateパッケージのLFS属性とSHA-256インストールを確認。
- 関連Edit Modeテスト60件成功。明示時刻と範囲外拒否、円移動の4経路、独立旋回・HMD非Clamp、Background停滞中の計画進行、計画予約枠、既存PoseTable／Dispatcher等を含む。
- 関連Play Modeテスト43件成功。既存Pose LOD、PoseTablePlayer、現在Pose切断、保留・再開・退役を含む。骨再生専用Tableの空Root trackとの互換、およびweight=0の補助骨を必須としない判定も確認した。結果は`Logs/MobPlan/editmode-final.xml`と`Logs/MobPlan/playmode-final2.xml`。
- Editorの32.32秒の試行で、実プレイヤーを2.894 m移動し、計画を32回公開。探索失敗0、古い結果の破棄1。移動中NPCの現在Pose切断を受理し、元個体のwithdrawal、20→19→新個体で20体への復帰を確認。
- 同試行のqueue p95=5.290 ms、worker実行p95=269.382 ms、回収待ちp95=2.881 ms、要求から回収までのp99=311.848 ms。探索だけでなくgoal field等のworker処理を含む。個別CSVは`Logs/MobPlan/cycles.csv`、実行ログは`Logs/MobPlan/play5.log`。

初回の切断試験はEditor更新から描画済みframeへ登録したため既存公開ガードに拒否された。検証ハーネスを通常のPlayerLoop Updateへ移して解消した。描画済みframeへの登録を許す変更はしていない。

## 未確認の範囲

Windows IL2CPP Playerでの専用worker計測を追加した。[計測結果](mobplan-il2cpp-worker-profile-2026-09-28.md)では20体・1 Hzで移動＋切断時のworker p95=236 ms、計画期限切れ0。一方で補充タイミングに200〜218 msの長いframeを観測した。

Quest／XR Simulatorでの性能計測、両眼90 Hz、実刀Gestureを使った目視確認は未実施。Editorおよび非XR Playerの統計からこれらの達成を主張しない。接続可能なOpenXR HMDがなかったため、この試験は非VR経路で行った。

旧Sandboxのcapsule occupancyは互換Fixtureとして残る。製品シーンの採用経路は共有mapであり、動的な切断開口・瓦礫をmapへ反映しない。未来数値Pose参照は用意したが、未来vertex skinning・投機切断のPhase 4.65／4.71／4.72を今回実装済みとはしない。
