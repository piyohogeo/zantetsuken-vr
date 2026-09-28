# Hit 登録数 19 と生存数 20 の差の切り分け（2026-09-28）

push はしていない。基準は main `332afa6a`。Building 系の未コミット変更は、別に作業ツリーに残っている。

## 結論

- 差は観測の違いによる。登録漏れではない。
- 19 と 20 は、命中の瞬間に、数える対象の違う 2 つの数を並べていた。
- 差になっていた 1 体は、その命中で切断を受け付けられた個体そのもの。
- 製品の変更はしていない。check に個体ごとの観測を追加し、焦点試験を 1 件追加した。

## 1. 数を照合する

**どこで出た値か**
- 19／20 は、check の `display track` 記録（`mobplan-record.txt`）の `lod=… detector=…` の値。
  - 書かれるのは命中の callback の中、つまり katana の Update の途中。
  - 終了時の記録は、保存した全 20 起動で `candidates at the end=20 max=20` だった。
- 保存ログでは、x12〜x25、r140731、smoke-live の全起動で、命中時の値が常に `detector = lod − 1` だった。1 回の起動に 2 件、`lod=20 detector=18` もあった。

**それぞれが何を数えているか（コード）**
- `lod`（PoseLod の登録数）：crowd の生存個体と同じ期間、登録されている。
  - 外れるのは、crowd の `RetireWithdrawn`（MobPlanCrowd の Update、順序 −75）で退役したとき（`LeaveLevelOfDetail`）。
  - 退役の条件は、handle の withdrawal（切断の公開）。
- `detector`（`SlashHitDetector.CharacterCount`）：切断を受け付けた時点で外れる。
  - 外すのは、`SlashHitDetector` の命中処理（katana の Update、順序 0）で、結果が Requested または Held のとき、または handle が破棄されたとき。
  - EmptySide、命中からの Full、Unavailable では外さない。
- **1 Frame の順序**：
  1. crowd の Update（−75）
  2. katana の Update（0）の命中：ここで detector から外れる
  3. 公開と withdrawal
  4. check の LateUpdate（400）
  5. 次の Frame の crowd の Update：退役し、LOD と計画から外れる

**保存ログでの対応**
- 根の切断の命中から退役までの Frame 差（`mobplan-events.csv`）：どの起動でも 0 か 1。
- 1 件だけ 1,779 Frame があった（x20 の r1002）。
  - 最初の命中が EmptySide で、その間も Hit の対象に残っていた。
  - 後の命中で切断された（命中から退役まで 1 Frame）。
- 保存ログには、個体ごとの Hit 登録が毎 Frame 残っていない。そのため、観測を追加した（下）。

**追加した観測**（check のみ。`SandboxPropSlashPlayerCheck.MobPlanHitRegistry.cs`、`MobPlan.cs` の呼び出し 5 か所）
- 各枠について、check の LateUpdate（その Frame のすべての Update の後）で次を対応付ける：
  - 枠、世代（Activations）、個体 id、handle（handle ごとの通し番号）、計画への登録、描画、LOD、Hit 登録
  - handle の切断の状態：ready／op／held／withdrawn／disposed
- 出力：
  - `mobplan-hit-registry.csv`：枠の状態が変わった Frame だけ書く。
  - `mobplan-hit-registry-frames.csv`：Frame ごとの数。
- 状態の分類：
  - `target`：活動中で、Hit 登録がある。
  - `cut-taken`：活動中で、切断を受け付けた、または要求が Held になったため、Hit 登録から外れている（handle が op／held／withdrawn／disposed）。
  - `UNREGISTERED`：活動中で、受付済み・Held・公開済み・退役中のどれでもないのに Hit 登録がない。
  - 休眠、退役済み。
  - 休眠・退役済みの個体、または切断を受け付けた個体が登録に残っている場合。
  - 検出器に、どの枠の現在の handle でもない登録がある場合（旧個体の登録残り）。
- 根への命中のたびに、その瞬間に Hit 登録から外れている生存個体を、名前と状態で記録する。
- scenario の判定を 2 件足した：
  - 活動個体のうち、受付済み・Held・公開済み・退役中を除いたものは、すべての Frame の終わりに Hit の対象だった。
  - 検出器が持っていたのは、生存個体の現在の handle で、受付済みでも Held でもないものだけだった。
- 「常に 20 件」という判定は作っていない。

## 2. 観測のずれか、登録漏れか

y1 の結果による（下の 4.）。

- **差になった個体**：
  - 根への命中 37 件のすべてで、命中の瞬間に Hit 登録から外れていた生存個体は、命中した個体 1 体だけだった（`only this hit:37`）。
  - その個体の handle は、公開で破棄されていた（disposed）。
- **Frame の終わりの状態**：
  - 検出器の数が生存数を下回った Frame は 37 で、根の切断の Frame 数と一致する。
  - 各 Frame で、外れていたのは 1 体だけ。状態は `cut-taken`。
  - 外れていた期間は、37 件すべてが 1 Frame で、すべて次の Frame の退役で終わった。
- **登録漏れ・登録残り・二重登録**：
  - 登録漏れ（`UNREGISTERED`）：0 Frame
  - 旧 handle の登録残り：最大 0
  - 休眠・退役済み個体の登録：最大 0
  - 切断を受け付けた handle の登録残り：最大 0
  - 検出器は `Contains` で二重登録を拒む。一致した数と `CharacterCount` の差（登録残り）は、最大 0 だった。
- **正しい handle で登録されていること**：
  - 見た handle は 57 個（初期 20 と、再利用 37）。
  - 例：枠 16 は a16（handle 17）→ r1000（handle 21）→ r1019（handle 38）。どの個体も、その枠の現在の handle で `target` になり、旧 handle は残っていない。
- **既存の理由による一時的な除外**（契約の変更はしない）：
  - Published（y1 の 37 件すべて）の場合：
    - 命中の Frame で、公開と withdrawal まで進む。
    - 描画は切断片に替わる。
    - crowd と LOD と計画からは、次の Frame の crowd の Update で外れる。期間は 1 Frame。
  - 命中時の受付枠不足（Full）：
    - 受け付けず、要求も保持しない。
    - Hit の対象に残る。
  - 表示入力の一時的な容量不足（Held）：
    - 命中時の表示入力の append が一時的な容量不足で拒まれた場合、受付前の要求を保持して Held を返す（`VpPreparedCharacterCut.Request`。この経路に `fromHit` による除外はない）。
    - `SlashHitDetector` は Held を返した個体を登録から外すので、Hit の対象から外れる。
    - 保持した要求は、後の更新で driver が取り上げる。
    - y1 では起きていない。check の `CutTaken` は `IsHeld` を含むので、`cut-taken` に数える。
  - 受付済みの Pending（予算不足）：
    - 再受付はせず、公開を待つ。Hit の対象から外れる。
    - 公開までは、受け付けた切断の入力が表示を担い、元の renderer は止まる。
    - root は公開まで残り、公開で withdrawal する。
    - 期間は公開までで、y1 では起きていない。焦点試験で確認した。
- **判定**：
  - check の数え方の違いであり、表示中の活動個体が理由なく Hit の対象から外れ続ける事象は見つからなかった。
  - 製品の変更は不要。check と記録を補正した（上の観測と判定）。

## 3. 焦点試験

`PreparedCharacterHitRegistrationPlayModeTests.cs`（`ProvisionalMassFlagActivationPlayModeTests` の partial、1 件）

- 準備した 2 体を登録する（1 体は 2 回登録しても 1 件）。
- EmptySide の命中：Hit 登録に残る。
- 切断を取る命中（Published）：
  - その評価の中で、その 1 体だけが外れ、もう 1 体は残る。
  - 次の Frame にも戻らない。
  - Commit の後、handle は破棄済み。
- 次の個体の handle を登録する（2 回登録しても 1 件）：
  - もう 1 体と並び、旧 handle は含まない。
- 次の個体を、Main の予算 0 で切る（Pending）：
  - 命中で外れる。
  - root は公開まで残る（その間も対象ではなく、同じ Slash の再受付もない）。
  - 予算を戻すと Commit し、withdrawal する。
  - 最後に残るのは、もう 1 体だけ。
- 予算の固定：
  - 最初の実行（h1）は、試験の Frame の Main 予算を使い切っていたため、最初の切断が Pending になり失敗した。
  - Published の段で `Driver.RemainingMainSeconds` を 1 秒に固定し、Pending の段は 0 にして決定的にした。
  - 製品の Held／Pending の扱い、DirectSkin の返却条件は変えていない。
- **結果**：
  - 焦点 h2：1／1
  - 同じ fixture と `PoseTablePlayerPlayModeTests`（h3）：38／38
  - 記録：`C:\log\zantetsuken-vr\MobPlanSlash\tests\h1-focused`、`h2-focused`、`h3-fixture`

## 4. 統合（y1：Player d20、Simulator v207、script-v1、予備の枠 0、目 1.6 m、95.7 s）

- exit 0。scenario 22 件がすべて ok（従来の 20 件と、上の 2 件）。
- 根の切断 37 件：初期の個体 20、再利用した枠の個体 17。子の再切断は 97 件。すべて Commit した。
- world は通常の終了で、すべて返した。geometry の不具合も終了要求もない。
- 終了時の NativeArray のリーク報告は 9,106 バイト（この単位では扱わない）。
- 差になる個体は、命中した個体そのものなので、どの命中も差になった個体を狙った入力にあたる。
- 性能の比較と 200 秒の起動はしていない。
- 記録：`C:\log\zantetsuken-vr\MobPlanSlash\y1\`
  - `run\mobplan-hit-registry*.csv`
  - `mobplan-record.txt` の `hit registry at hit`
- build：`build-d20`
  - PC_RPAsset は戻した。設定 30 件の hash が一致した。
  - コードの状態：`code-state-hr\`。build の後、check のコードは変えていない。

## 訂正（TL レビュー後、2026-09-28）

- 最初の提出では「命中から Held にはならない」と書いたが、コードと異なっていた。表示入力の一時的な容量不足による Held の経路がある（上の 2.）。
- 訂正は説明と判定の文言だけで、製品の動作と check の分類（`CutTaken` は `IsHeld` を含む）は変えていない。
  - check の判定文を、受付済み・Held・退役中などを除いた活動個体と分かる表現にした。
  - 試験の説明にあった「唯一の窓」という表現を改めた。
- 文言だけの修正なので、build・試験・統合の起動はやり直していない。y1（d20）の判定文は、訂正前の文言のまま。

## 残件（この単位には広げない）

- 集約表示の中の片の退役制限
- 直進距離の差
- NativeArray の 510 バイトの差
- Link での容量境界越え
- 退役通知の同 Frame 経路（起動の中では観測していない）
