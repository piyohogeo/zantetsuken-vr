# WORLD CUTTER PCVR版の利用案内

Windowsで動く、街とNPCを含む通常プレイ版です。右手の刀で街の物や人物を切断できます。人型キャラクターの切断・欠損表現を含みます。

この案内はプレリリース **0.1.1-alpha.1**（タグ `pcvr-v0.1.1-alpha.1`）向けです。配布情報と添付ファイルは [GitHub Releases](https://github.com/piyohogeo/zantetsuken-vr/releases) で確認してください。

## 対応環境と確認範囲

- Windows 10 / 11（x64）
- Meta Quest 3 / 3S とTouchコントローラー
- PCのMeta Quest Linkアプリと、Meta Quest Linkを既定のOpenXRランタイムにした環境
- Direct3D 11またはDirect3D 12が使えるGPU

確認したヘッドセットはMeta Quest 3Sです。GPUはRX 570、RX 5500 XT、GTX 1650 SUPER、GTX 1660 SUPER、RTX 2060 SUPER、RTX 3090での記録があります。CPU、OS、ドライバー、描画API、解像度を含む結果は [PCVR性能評価](pcvr-performance-2026-11-08.md) を参照してください。

表示周期はLink側の設定を使います。90Hzで街歩きと性能を確認しました。80Hzと120Hzの通常プレイ・性能は未確認です。起動時の描画準備が完了したことだけでは、その表示周期での動作確認にはなりません。

## 導入と更新

1. [GitHub Releases](https://github.com/piyohogeo/zantetsuken-vr/releases) でPCVR版のReleaseを開き、添付のWindows x64用ZIPをダウンロードします。
2. ZIP全体を任意のフォルダーへ展開します。更新時は新しいフォルダーへ展開してください。
3. QuestをUSB LinkまたはAir Linkで接続し、ヘッドセット内でLinkを開始します。
4. 展開したフォルダーの `Zantetsu.exe` を起動します。

`Zantetsu.exe` と `CutWorldSandbox.exe`、データフォルダー、DLLなどを同じ配置のまま使用します。通常の起動にはUnity Editorの導入は不要です。

### SmartScreenが表示された場合

このプレリリースの `Zantetsu.exe`、`起動設定.exe`、`CutWorldSandbox.exe` はコード署名を付けていません。初回起動時などに、Microsoft Defender SmartScreenの「WindowsによってPCが保護されました」や「不明な発行元」という警告が表示される場合があります。[Microsoftの説明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)

このリポジトリのGitHub Releasesから入手したファイルであることを確かめ、実行する場合は次の手順で進めます。

1. 警告画面の「詳細情報（More info）」をクリックします。
2. 表示されたアプリ名が、起動したexeまたは同梱の `CutWorldSandbox.exe` であることを確認します。
3. 「実行（Run anyway）」をクリックします。[Microsoftの実行手順](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/publish-first-app#step-6-handle-smartscreen-for-new-apps)

「実行」が表示されない場合は、PCの管理者または [GitHub Issues](https://github.com/piyohogeo/zantetsuken-vr/issues) へ、表示されたメッセージを連絡してください。

## 遊び方

| 操作 | 内容 |
| --- | --- |
| 左スティック | 初期設定では前後・左右へ移動。移動方向は頭の水平方向の向きに従います |
| 右スティックの左右 | 初期設定では30度ずつ旋回 |
| 右手の刀 | コントローラーを振ると斬撃が飛び、当たった対象を切断 |
| 左コントローラーのメニューボタン | メニューを開閉。開いている間はゲームが停止 |
| 右手のポインターとトリガー | メニューの項目を選択・変更 |

メニューでは音量、移動速度、スティック配置、スナップ／連続旋回、刀の持ち位置・角度などを調整できます。`Right stick turn` をOFFにすると、左スティックの上下で前後移動、左右で旋回する配置になります。

メニューやシステム画面から戻った後は、トリガーやスティックをいったん離してから操作を再開してください。

終了するには、Windows上でゲームのウィンドウを閉じるか、Linkのシステム画面からアプリを終了します。

## 描画方式の設定

通常は `Zantetsu.exe` が描画方式を自動選択します。

| 条件 | 自動で選ぶ方式 |
| --- | --- |
| Radeonを単一のGPUとして判定できた場合 | 互換 Direct3D 11 |
| Radeon以外、GPUが複数、判定できない場合 | 標準 Direct3D 12 |

自動判定はRadeon全モデルに同じ方針を適用します。既知のMeta Virtual Monitorは物理GPUの候補数から除外します。

画面が黒くなる場合や描画方式を指定したい場合は、ゲームを終了して `起動設定.exe` を開いてください。ヘッドセットを使わずに操作できる日本語の設定画面です。「自動（推奨）」「標準 Direct3D 12」「互換 Direct3D 11」から選び、「保存して起動」を押します。`Zantetsu.exe --settings` でも同じ画面を開けます。

VR内のメニューにある `Graphics API` からも保存できます。設定は次回の起動から反映されます。`Current` の表示で今回使っているAPIと選択理由を確認できます。

選択の優先順位は、明示した起動引数、保存した設定、自動判定の順です。手動で引数を指定する場合は、`Zantetsu.exe -force-d3d11` または `Zantetsu.exe -force-d3d12` を使います。

## 任意のプレイ統計

初回の `Play statistics` 画面で、性能と安定性の改善に使う統計の送信を選べます。同意は任意で、`Don't send` を選んでも遊べます。

左メニューの `Send play statistics` で、いつでも撤回・再同意できます。収集項目、フォーカス喪失時・通常終了時の送信、保存期間は [PCVR版プライバシーポリシー](pcvr-privacy-policy.md) を参照してください。

## 素材とライセンス

配布版には、個別ライセンスの都市・人物素材をアプリに組み込んでいます。これらの素材には提供元の条件が適用されます。第三者表記は配布ZIPに同梱する `ThirdPartyNotices.txt` で確認できます。

公開ソースコードにはMIT Licenseが適用されます。公開リポジトリからビルドするStanford Bunnyデモには、Stanfordの非商用条件が適用されます。それぞれの範囲は [READMEのライセンス](../README.md#ライセンス) を参照してください。

## 問い合わせ

開発者の公開名は **hogehogeo**、問い合わせ先は [GitHub Issues](https://github.com/piyohogeo/zantetsuken-vr/issues) です。不具合の連絡にはアプリの版、OS、CPU・GPU、ドライバー、ヘッドセット、Linkの表示周期と解像度、描画API、再現手順を添えてください。

Issuesの投稿は公開されます。ログを添付するときは、個人情報やPC内の個人用パスなどを確認してください。
