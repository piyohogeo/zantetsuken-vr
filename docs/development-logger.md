# 開発用簡易ロガー

`DESIGN.md` 21.17 の managed 診断は `Zantetsu.Core.DevelopmentLogger` を使用する。

```csharp
#if DEBUG
DevelopmentLogger.Instance.write_log("slash-tuning", "speed", speed);
DevelopmentLogger.Instance.write_log("slash-tuning", "note", "確認用の文字列");
DevelopmentLogger.Instance.write_log("slash-tuning", "samples", EnumerateSamples());
#endif
```

`value` は C# の整数型、`float`、`double`、`decimal`、文字列、またはそれらの `IEnumerable` / `IEnumerator`。列挙は呼出元 thread で完了し、iterator を dispose する。`null` は null、空の列挙は空配列となる。非有限数、未対応型、列挙中の例外は record 全体を破棄する。writer_id / tag はそのまま保存し、登録や正規化はしない。

戻り値は `DevelopmentLogResult`。`Accepted` はキュー受付成功、`QueueFull` は満杯、`Unavailable` は session 不在・停止中・I/O 失敗後、`InvalidValue` は値変換の失敗、`Disabled` は非 `DEBUG` を表す。既存呼出しは戻り値を無視し、エラーハンドリングは追加しない。`Accepted` は保存完了を保証しない。後から Worker で起きた I/O 失敗は既に返した結果を変更できず、その session の以降の受付を停止する。

Unity 標準 `DEBUG` だけで有効になる。[C# の Conditional 属性は戻り値が void の場合だけ使用できる](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/attributes#2353-the-conditional-attribute)ため、呼出しを `#if DEBUG` で囲んで引数評価ごと除去する。既存呼出しもこの形式へ変更済み。ガードのない呼出しは非 `DEBUG` でも引数を評価するが、API 本体は列挙・記録せず `Disabled` を返す。

各 Play 開始 / Development Player 起動時、`C:\log\zantetsuken-vr\logger` に UTC 開始時刻・コミットハッシュ・ランダム接尾辞を持つ新規ファイルを作る。Editor は作業先の Git HEAD、Player は build callback で StreamingAssets に渡した HEAD を使用する。ハッシュは未コミット差分を表さない。Git / build metadata の取得失敗時は記録を開始しない。

初期保存形式は BOM なし UTF-8、LF 終端の `.jsonl`。各行は `time`, `frame`, `writer_id`, `tag`, `value` の5項目。`time` は UTC POSIX 秒を小数点以下7桁の数値で保存する。`frame` は Main の早い Update で取得した `Time.frameCount` の最新値で、worker と描画の厳密な対応は保証しない。

完成した行を既定65,536件のキューへ投入する（`DefaultQueueCapacity`、session 開始時の `queueCapacity` 引数で変更可能）。容量は待機行の件数で、Worker が書込み中の最大1件を含めない。満杯なら新規 record を拒否し、空きを待たず `QueueFull` を返す。呼出元で iterator を保持したまま Worker へ渡すことはない。

session ごとの専用 Worker がファイルの作成・open・書込み・close を行い、`AutoFlush = true` でキューの行を順番に追記する。キュー操作の lock はファイル I/O 中に保持しない。読取り側は Windows の共有書込みを許可する（.NET なら `FileShare.ReadWrite`）。LF 終端の正常な行だけを読み、末尾の未完了部分は無視する。

停止・再開始では旧 session の受付を停止して Worker に排出・close を要求する。標準終了通知からの join は最大1秒で、I/O が止まった場合はそれ以上待たない。timeout 後も旧 Worker は旧ファイルだけを所有し、新 session へ書かない。終了時の全件保存は保証せず、記録呼出しからの自動再生成もしない。

Slash の Dump / Auto dump on latch と Capture 保存通知をこの API に移した。Dump 本文の整形と操作 UI、Capture の入力・条件・再計算比較結果は維持する。構成不備や描画資源失敗を知らせる既存エラー、Editor の検証結果・試験成果物は引き続きそれぞれの既存経路を使用する。

既存の Profiler・Trace・Capture、必須 Trace 記録、共通 Player 終了要求は変更しない。本ロガーは任意診断専用であり、Trace の代替や終了要求の前提にしない。ユーザーパスを値へ含める側は `%USERNAME%` で匿名化し、ライセンス Asset の公開境界を守る。

Unity lifecycle は `SubsystemRegistration` と標準終了通知へ接続する。Player 用コミット情報は [Unity の追加 StreamingAssets API](https://docs.unity.com/en-us/engine/6000.3/manual/building-and-publishing/streaming-assets) を使用し、追跡対象 Asset や Player 設定を生成・変更しない。

2026-09-20、Worker 化前の固定版 Unity 6000.3.22f1 で対象 EditMode テスト171件が成功した。Domain Reload / Scene Reload をともに無効にした Play の2回開始・終了、新規ファイルと Writer の閉鎖を確認した。

Worker 化前には一時的な最小シーンで x64 IL2CPP Development / 非Development Player をそれぞれ build・短時間実行し、両方が終了コード0。Development はコミット情報、Main / worker の3 record、桁保持、フレーム更新を確認した。非Development は引数評価0回、新規ログ0件、ロガー用コミット情報の同梱なし。これはロガーの機能確認であり製品性能・XR シナリオの評価ではない。

同日の Worker 化後は既存 Slash テスト165件とロガーテスト9件が成功した。Worker の I/O を意図的に停止し、呼出元が保存を待たないこと、並行投入65,550件のうち65,536件だけを受け付け残り14件が `QueueFull` になること、Worker 上での open・write・dispose、終了待ちの打切り後に旧・新 session が混在しないことを確認した。別プロセスからの開いたファイルの読取り、400件の並行書込み、数値精度、非 `DEBUG` のガード内引数評価0回・`Disabled`・列挙なしも独立した C# 実行で確認した。Worker 化後の IL2CPP Player build は再実行していない。
