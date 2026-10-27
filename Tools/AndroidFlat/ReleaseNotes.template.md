# WORLD CUTTER Android @VERSION@

Androidの非VR版の初期検証版です。縦画面・横画面に対応しています。人型キャラクターの切断・欠損表現を含みます。

## 導入と操作

Android 12L（API 32）以降・ARM64・Vulkan対応端末向けです。

1. Assetsから **@APK@** をダウンロードし、ブラウザまたはファイル管理アプリから開きます。
2. Androidが求めた場合は、その提供元からのインストールを許可し、端末の検査・確認画面に従って導入します。
3. WORLD CUTTERを起動します。更新も新しいAPKを開く手動方式です。同じ正式署名の版からは設定を保持して更新できます。

左下の円で前後移動と旋回、操作UI以外の画面のフリックで切断します。Menuで移動・描画・ジョイスティックの配置を設定できます。Joystick positionは縦画面・横画面で別々に保存します。Apply renderingとReset sceneでは切断状態がリセットされます。

統計送信は任意で、同意しなくても遊べます。Menu > Statisticsで変更し、Privacy policyで通信せずに説明を読めます。

- [導入・操作の詳しい案内](https://github.com/piyohogeo/zantetsuken-vr/blob/main/docs/android.md)
- [プライバシーポリシー](https://github.com/piyohogeo/zantetsuken-vr/blob/main/docs/android-privacy-policy.md)
- 公開名：hogehogeo／[問い合わせ：GitHub Issues](https://github.com/piyohogeo/zantetsuken-vr/issues)

## 確認範囲と既知の制限

| 項目 | 確認した範囲 |
|---|---|
| Galaxy S25 Ultra | 今回の候補で縦画面の起動・描画、横画面の操作・メニュー・Slash表示・ホームからの復帰 |
| Pixel 6 Pro | 今回の候補で更新インストール、縦横の操作・切断・破片表示、設定保持、ホームからの復帰 |
| Pixel 7／Mali-G710 | 今回の候補でリモート実機の縦画面描画、フリック後の人物の破片・倒れた木の表示と設定メニューを短時間確認。断面の正しさ・横画面・長時間性能は未確認 |
| Pixel 10／PowerVR | 今回の候補でリモート実機の縦画面描画、フリック後の人物の破片・倒れた木の表示と設定メニューを短時間確認。断面の正しさ・横画面・長時間性能は未確認 |
| 長時間負荷 | graph導入前の旧検証用ビルド（56e463a9）でS25縦・Pixel横とも30分の連続操作を完走し、容量不足なし。今回のgraph有効版では未確認 |
| 16KB環境での実行 | 旧code 6で公式エミュレーターとGalaxy A56 5Gの縦画面の起動・切断・中断復帰を確認。今回の候補は16KB整列検査に合格していますが、16KB環境での実行は未確認 |

旧版のPixel長時間試験では、操作終了後の最終採取で電池温度44.0℃・熱状態4に達しました。Pixelの熱負荷の余裕や常時60FPSの維持は確認済みではありません。A56の横画面と、他端末の動作も未確認です。

S25 Ultra、Pixel 6 Pro、Pixel 10で既知の `DexFile.finalize` 警告を記録しています。警告後もゲームの継続を確認していますが、原因は未確定です。
Pixel 7とPixel 10では別の `SwappyDisplayManager` による `libgame.so` 探索失敗を記録しましたが、描画と切断後の表示は継続しました。Mali CSF世代やPowerVR全体への対応保証ではありません。

## 配布ファイル

- APK：`@APK@`
- アプリ版：`@VERSION@`／versionCode **@CODE@**
- APK SHA-256：`@HASH@`
- 署名証明書SHA-256：`@CERT@`
- 公開タグ：`@TAG@`（APKのビルド元コミットを対象にします）
- APKのビルド元：[ソースコード @SOURCE@](https://github.com/piyohogeo/zantetsuken-vr/tree/@SOURCE@)

AssetsにはAPK、同名の `.sha256`、`signing-certificate-sha256.txt`、`ThirdPartyNotices.txt` を添付します。第三者表記はAPKにも同梱しています。
