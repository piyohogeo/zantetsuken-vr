# 頂点範囲の回収と再利用（DESIGN §4.5.3）実装記録（2026-09-28）

ローカルコミット済み、push なし（V1 `5555a936`、V2 `df59c07c`。下の「コミット」）。基準は main `8e1b7019`。Building 系の未コミット変更は、別に作業ツリーに残っている。

## 方式（§4.5.3 の保守的な方式）

**頂点グループ**
- ObjectId（系譜）ごとの頂点範囲を 1 つのグループとして扱う（`VpCpuGeometryStorage.VertexGroups.cs`）。
- 根の append（prepared／cuttable／mesh／DirectSkin、頂点がある場合）がグループを開く。その系譜の切断は、Commit で残した範囲（頂点・submesh・vertex block）を同じグループに加える。
- 切断の子は親の block 列を引き継ぐので、どの geometry も先頭の block は根のもの。その block の開始位置をグループの鍵にする。
- グループには、その geometry が公開した index range と、グループの上で開いている切断予約の数も記録する。

**静かなグループ**：予約が開いておらず、公開した index range がすべて Free（退役済みで lease もない）。そのとき：
- 描画は読めない。描画は Geometry 参照で range を Published に保つため。
- 切断の Work も読めない。入力 range に lease を持つため。
- 転送は、生存する geometry に対してしか行わない。
- 新しい切断もできない。入力に Published が要るため。
- metadata も、range が Free なら読めない。

**解放の順序**
- storage は、range の退役、lease の返却、予約の終了のときに、そのグループを「見直し」に回す。
- `CutDag.ReclaimVertexRoom()` を `CutWorldRoot.LateUpdate` から毎 Frame 呼ぶ。見直しに回ったグループのうち、静かで、かつ系譜の根に生存する LogicalFragment がない（`BranchHasAReader(root)` が false）ものを、`TryReleaseVertexGroup` で一度だけ返す。
  - 根は `RegisterBaseGeometry` で記録する。
  - 系譜がまだ生存しているグループは、ledger が変わったときに見直す。
- 返した範囲は既存の allocator（first-fit、結合）へ戻り、次の append や切断の予約に、そのまま再割当される。内容は消さない。
- 片の寿命管理の有効・無効とは関係なく動く。切断の置換、Abort、寿命管理による退役の、どれもこの経路で終わる。

**含めないもの**
- CPU の commit 量、GPU の容量、ページの OS 返却は縮めない。
- GPU の完了も待たない。

**GPU について**
- 再利用した offset には、新しい geometry の表示登録のときに、その geometry 自身の block（または切断で足した block）を転送する。GPU の拡張時の再転送も、生存する geometry だけを対象にする。
- offset ごとに「転送済み」と記録して転送を省く仕組みは、もともとない。
- 旧い描画との前後関係は、コマンド列の順序と、採用の後に登録を手放す順序で保たれる。

## 既存経路への最小の接続（見つかった不足）

- **表示の登録の手放し**：`VpLogicalCutDisplay` は、登録の fragment が Retired のときだけ登録を手放していた。
  - そのため、切断の Final 公開の後、Geometry Commit の前に両側が退役すると、置換された元の登録と Geometry 参照が残り、系譜の頂点範囲が永久に保持されていた。
  - Replaced で、下に生存する fragment がないときも、次の収集で手放すようにした。
  - 下に生存する片がある Replaced（公開から Commit までの通常の状態）は、これまでどおり保持する。
- **prepared root の表示登録の失敗**：`TryShowPreparedRoot` が拒否しても、append 済みの geometry は呼び出し側の所有に残る（表示が geometry を登録するのは最後の段で、拒否はすべてその前）。これまで `VpPreparedCharacterCut.Request` の失敗の分岐は、その range を返さずに終わっていた。
  - 失敗の分岐で、request が `TryRetireIndices` によって range を一度だけ退役させるようにした。
  - 表示の側は、この geometry を登録していないので退役させない（二重退役にならない）。
  - 根は CutDag に登録されていないので、グループは静かになった時点で返る。
  - 試験用に、表示の側に早い拒否の hook（`RefusePreparedRootShowForTest`）を足した。
- storage：`RecordAppend` でグループに参加する。予約の開始・終了でグループの予約数を数える。`TryRetireIndices` と `TryReleaseIndexReadLease` でグループを見直しに回す。`DescribeRoom` にグループの状況を出す。

## 試験

- **EditMode**（`VpStorageCutOutputTests.VertexGroups.cs`、3 件。既存の fixture を partial にした）
  - 親、どちらかの側、退役した側への lease のどれかが読めるうちは保持する。すべて Free になったら一度だけ返す（二度目は拒否）。次の append が、根のあった位置を再利用する。
  - 開いた予約はグループを保持する。予約の終了で見直しに回る。
  - ある系譜の解放は、別の系譜の頂点・range・metadata に影響しない。
- **PlayMode**（`CutWorldRootPlayModeTests.VertexRoom.cs`、2 件）
  - 片側を退役しても、兄弟が生存していれば保持する。両側を退役すると一度だけ返す。
  - 次の body は、ローカル頂点を (0.3, 0, 0.2) ずらした箱で、旧い内容と頂点が異なる。
    - 同じ頂点範囲を使う。
    - GPU 上の頂点は新しい内容と一致し、旧い内容とは異なる。
    - 残っている別の系譜の頂点は、CPU・GPU とも変わらず、描画され続ける。
    - 新しい body は通常どおり切断できる。
  - 退役後もまだ読んでいる Geometry Work が、終わるまで範囲を保持する（上の表示の登録の不足は、この試験で見つかった）。
- **PlayMode**（`PreparedCharacterShowRefusedPlayModeTests.cs`、1 件）：同じ model の 2 体のうち、A の append の後に表示登録を拒否する。
  - A の range と頂点グループは残らない（グループの解放が 1 回、空き容量が元に戻る）。
  - B の切断が、ちょうどその範囲を使い、high-water は動かない。
- **関連**：
  - 最初の提出時：寿命管理と頂点範囲 8／8、表示・DAG・Commit・storage 169／169、単位の定例 221／221・90／90、storage・切断・backing の関連 309／309、World の backing・表示容量 4／4。
  - 補正後：焦点 3／3、storage・表示の EditMode 308／308、World と prepared character の PlayMode 46／46。

## 統合（x25：Player d19、v207、script-v3、しきい値 64、目 1.6 m）

- 結果：exit 0、scenario 21 件がすべて ok、例外なし。
- 17 系譜の頂点範囲が返った（107,762 頂点）。最初の解放は t=106 s（直進を始めた後）。
- **使用量と high-water の推移**：同じ範囲の再利用を直接確かめたのは焦点試験（上）。統合では推移だけを示す。
  - frame 7946 → 8029：使用量は 257,671 → 262,875（+5,204）、high-water は 274,728 → 275,204（+476）。high-water の伸びが使用量の伸びよりずっと小さい。
  - frame 8029 → 9959：high-water は 275,204 のまま。この間の返却（使用量は 179,694 まで減少）の後も、使用量はまた増えた（9876 → 9959 で +5,187）。
  - この推移は、後の append が返った範囲に入ったことと整合する。
  - その後、空き span に収まらない append では high-water が伸びた（first-fit で、compaction はしない）。
- commit 量（524,288）と GPU の容量は変わらない（設計どおり）。

## 残件

- 退役通知の同 Frame 経路：起動の中では観測していない。
- 集約の中の片を退役させない制限。
- 直進距離の差。
- Hit 登録数 19 と生存数 20 の差。
- NativeArray の 510 バイトの差。
- Link での容量境界越え。

## 採用（TL、2026-09-28）

- **採用した範囲**：
  - 生存する片・描画の参照・Work・予約による保護
  - 使われなくなった系譜の頂点・submesh・block 範囲の返却と再利用
  - 生存する子孫がない Replaced 登録の解放
  - prepared root の表示登録を拒否したときの回収
- **確認の区別**：
  - 同じ範囲の再利用と、GPU の内容の更新：焦点試験で確認した。
  - 統合：使用量と high-water の推移で確認した。
- **残件の扱い**：
  - 頂点の再利用の残件は、これで解消とする。
  - 集約の中の片を退役させない制限などが残るため、§7.10 全体の完了や、長時間 Playable の受け入れの完了とはしない。

## コミット（ローカル、push なし）

- **V1 `5555a936`**（親 `8e1b7019`）：19 ファイル。
  - 製品 9：
    - 変更 8：`VpCpuGeometryStorage.cs`、`VpDirectSkinStorage.cs`、`VpCutOutputReservation.cs`、`CutDag.cs`、`VpLogicalCutDisplay.cs`、`VpPreparedRootDisplay.cs`、`CutWorldRoot.cs`、`VpPreparedCharacterCut.Request.cs`
    - 新規 1：`VpCpuGeometryStorage.VertexGroups.cs`
  - 試験 5：
    - 変更 2：`VpStorageCutOutputTests.cs`、`CutWorldRootPlayModeTests.cs`
    - 新規 3：`VpStorageCutOutputTests.VertexGroups.cs`、`CutWorldRootPlayModeTests.VertexRoom.cs`、`PreparedCharacterShowRefusedPlayModeTests.cs`
  - `.meta` 4：新規 4 ファイルの分。
  - 記録 1：この文書。
- **V2 `df59c07c`**（親 `5555a936`）：計測 check の観測だけ。
  - `SandboxPropSlashPlayerCheck.MobPlanLifetime.cs`
  - mobplan-lifetime.csv の 4 列：`freeVertexRoom`、`vertexGroups`、`vertexGroupsReleased`、`verticesReleased`
- **照合**：
  - ステージ時：試験した状態の差分と一致した。
  - コミット後：`git diff HEAD~1 HEAD` が、ステージした差分とバイト単位で一致した（V1・V2 とも）。
