# NVENC two-worker 停止の修正記録

2026-09-12。Unity 6000.3.22f1 / D3D11 / RTX 3090 / NVIDIA driver 591.86 / Video Codec SDK 13.0.37 で調査。

3つの問題を切り分けた。

1. **UnityとNVENCが使うimmediate contextのマルチスレッド保護がOFFだった。** 詳細ログを抑えた反復試験の4回目で、元の報告と同じ `Present → D3D11 → nvwgf2umx → wait` の停止を再現した。時間を隔てた2回のstackでも同じ待機位置を確認した。render callbackで、変換命令の投入前に `ID3D11Multithread::SetMultithreadProtected(TRUE)` とread-backを行う。`SINGLETHREADED` deviceや取得失敗は変換失敗として返し、NVENC submitへ進ませない。共有deviceの保護をsession終了時にOFFへ戻さない。
2. **Main Threadのsource返却が、既に終わったOutput処理の資源に依存していた。** 返却handoffの受付時には全資源の有効性を確認するが、Mainでの適用時にWork/Sample/両creditのactive状態まで再要求すると、Outputが先に進んだ際に永久に返却できない。適用時は元のsource所有権、正確なpending sync世代、immutableな変換完了証拠、非poison状態を既存gate内で確認する。別frameに再貸出された下流資源には触れない。
3. **resource-resolution gate競合後にOutput Workerを起こす配線がなかった。** 上の2件を入れた後も、実機sentinelが間欠的にtimeoutした。30秒のwall-clock期限で採取したstackでは、`ZantetsuNvenc` およびNVENCの中にいるthreadは1本もなく、両workerはmanagedのwait内でparkしていた。native copyから正常に戻った後、Access Unitのcommitがこのgateを取れないとmanaged側が `Pending` を返し、collectorがrecordとproofを保持してfalseを返し、workerがparkする。gateが解放されても誰も起こさないため再試行されない。共有gateの解放をすべて1箇所に集約し、解放後に、composition時へ1度だけ束ねたOutput Workerへ既存と同じcoalescing wake hintを送る。束縛の解除はそのworkerが物理停止した後だけ許す。

NVIDIAはDXGIと別threadのbitstream取得の併用に注意を記載している。ただしdriver内部の待機オブジェクトまでは特定しておらず、あらゆるdriverで同じ内部原因だと結論していない。[NVIDIA SDK 13.0 threading model](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-video-encoder-api-prog-guide/index.html#threading-model)

**比較結果**

| 試験 | 結果 |
| --- | --- |
| `baseline-01` | encode / append / frame進行後、未freezeのcontextをfinalizeしようとする試験側の不足でtimeout。試験を修正。 |
| `baseline-02` | 単発は10/10成功。保護OFFの単発成功だけでは停止を検出できなかった。 |
| `baseline-quiet32` | 3回完了後、4回目で停止。元のPresent停止と同じstackを2回採取。 |
| `protect-quiet32` | 保護ONを実測。7回完了後、source返却の競合を検出。Mainのwatchdogが動き、元のPresent停止とは区別できた。 |
| `releasefixed-off32` | source返却修正後も3回完了後にPlayer進行停止。ただしこのstackはUnity内のwaitで、元のPresent内waitと同じとは判定していない。 |
| `releasefixed-protect32` | 同じDLL・同じ試験ソースで保護だけON。32/32回のencode、回収、8 frame進行、finalize、Worker物理停止、資源解放に成功。Standalone全10件成功。 |
| EditMode全件 | source返却の再利用競合と不正handoffの回帰を含む6,955/6,955成功。 |
| WARP native contract | 保護OFF→ON、繰返し、null入力、SINGLETHREADED device拒否を検証して成功。 |

ログ・2回のstack・比較manifest・テストXMLは、ローカルの `original/rescue-experiments/` に保存した。端末固有パスを含むためGit対象外。比較試験の `enableEncodeAsync=1 / enableOutputInVidmem=0 / doNotWait=0` は元から同じで、追加の `Enter/Leave` 出力scopeは使っていない。

**gate競合後のwakeの検証**

| 試験 | 結果 |
| --- | --- |
| 決定的EditMode試験 | 実Submit／Output Workerとfake output sourceを組み、別threadがcopy帰還をまたいでgateを保持して競合を作る。gate保持中は進まず、gate解放だけでrecordが再開してSink appendとFrame Completionまで収束。output source呼出し1回、append 1回、Completion 1回、FIFO不変、poison／fatalなし。 |
| 効力確認 | wakeを外すとこの2件だけが `the parked record did not resume when the gate was released` で失敗し、他は影響を受けない。 |
| Standalone再判定 | wall-clock 30秒期限のsentinelを新しいPlayer processから連続実行し、25回すべて10/10成功。停止の再現なし。Player残留、`InitTestScene*`、repository内build副作用もすべての回でなし。 |

修正前は、同じwall-clock 30秒期限のsentinelで23回中2回、iteration 1でtimeoutしていた。そのときの記録は `outQ=0 outputPending=True outputStopped=False submitStopped=False fatal=none appends=0` で、mainは30秒のあいだ18万から22万frameを進めていた。

**回帰試験の扱い**

実機sentinelは実際のSubmit/Output Workerとproduction adapterで1 frameの処理を繰り返す。ログによって競合が隠れないようWorkerの段階ログを外し、Mainの開始・終了・失敗情報を残す。正常時は両Workerの物理停止と資源返却の証拠を確認してからnative資源を解放する。異常時はpoison / Notifyだけで停止や非使用を推測せず、owner graphを保持して専用Playerの終了に委ねる。

環境変数による診断切替と診断exportは最終実装から除去した。通常ビルドで保護が有効になる。`SetMultithreadProtected` の戻り値は変更前の状態なので、成功判定には `GetMultithreadProtected` を使う。[Microsoft API仕様](https://learn.microsoft.com/en-us/windows/win32/api/d3d11_4/nf-d3d11_4-id3d11multithread-setmultithreadprotected)

**最終版の検証**

- Native Releaseビルドと、実装と同じhelperを使うWARP contractが成功。生成DLLを `Assets/Zantetsu/Plugins/x86_64/ZantetsuNvenc.dll` に反映した。
- `final-normal32`（Run ID `20260912-185223-b6b84d`）はStandalone **10/10成功**。`Player_RepeatsOneFrameThroughBothWorkers` は32回の開始と32回のteardown完了を記録し、保持が必要な失敗は0件。実際の試験時間はXML上1.56秒、ビルドを含むrunner経過時間は26.2秒。
- 最終DLLのSHA-256は `24DB31AE7C5BD3FEC4820F1D5816332322FB276F328958B49752474876DD1747`。試験Player配置先のDLLも一致。そのときのsentinelのSHA-256は `FBF0A36BAF029DC111A8C8BE8E70330A08683AC91A85DAC0DFE10DEC77D9981E`。期限をwall-clockだけにしwakeの束縛を加えた最終sentinelは `D351F50C910364ED4AF8808D8248E53F1E8B55622DF18383A72C2FD541159828`。
- EditMode全件のRun IDは `20260912-184327-782c9a`、**6,955/6,955成功**。wake修正とそのcontract試験3件を加えた最終状態では、Run ID `20260912-195324-cb6dab` で **6,958/6,958成功**。返却後に同じslotを次世代として再貸出した状態でも、古いsource返却が次のframeを壊さない回帰試験を含む。
- 最終runのmanifestにある診断用環境変数は既存採取runnerの記録であり、最終実装は参照しない。反復回数も試験コードで32回に固定した。

停止の再現と解消を確認したのは上記環境とこの1-frame反復負荷である。長時間の連続録画、別GPU・別driverでの結果までは含まない。共有contextの保護による描画性能への影響はこの試験では測定していない。

変更箇所: [D3D11保護](../../../Native/ZantetsuNvenc/D3D11ThreadProtection.h)、[render callbackへの適用](../../../Native/ZantetsuNvenc/NvencEncoderSession.cpp)、[source返却](../../../Assets/Zantetsu/Runtime/Observability/NvencSourceResourceReleaseCoordinator.cs)、[pending sync検証](../../../Assets/Zantetsu/Runtime/Observability/NvencGpuConversionSyncPool.cs)、[返却の回帰試験](../../../Assets/Zantetsu/Tests/EditMode/NvencSourceResourceReleaseCoordinatorContractTests.cs)、[実機反復試験](../../../Assets/Zantetsu/Tests/Standalone/NvencNativeGraphicsObservationSentinelTests.cs)、[gate解放後のwake](../../../Assets/Zantetsu/Runtime/Observability/NvencCaptureProcessState.cs)、[wakeのcontract試験](../../../Assets/Zantetsu/Tests/EditMode/NvencResourceResolutionGateWakeContractTests.cs)。
