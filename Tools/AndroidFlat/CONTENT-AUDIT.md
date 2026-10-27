# Android APKの梱包・禁止語検査

`Audit-DistributionContent.py` は検査済みAPKとローカル配布一式を読み取り専用で検査します。禁止語はリポジトリ外のUTF-8一覧を `--forbidden-words-file` またはGit設定 `zantetsu.forbiddenWordsFile` から読み、本文を出力しません。監査JSONと依存ツールは公開用の添付物へ含めません。

## 検査する内容

- APK全体、ZIPの名前・コメント・全展開エントリー
- 大文字小文字を区別しないUTF-8、UTF-16 LE／BE、UTF-32 LE／BEの文字列
- Unityの分割ファイルを連結した境界、JSON文字列のUnicodeエスケープ
- ELFの圧縮デバッグ節、PNGの圧縮テキスト・プロファイル情報
- UnityのTextAssetとシェーダーのメタデータ、全LZ4シェーダーセグメント
- SMOL-Vを復元したSPIR-Vと、DXCで逆アセンブルしたDXIL
- 既存監査と元のビルドログの素材一覧の差分、配布一式の名前と内容

検査器自身も、符号化・分割境界・JSON・LZ4・SMOL-V・DXILの陽性対照と陰性対照で動作を確認します。検査失敗・未対応形式・復元失敗は合格にしません。画像の画素からのOCRと任意の暗号化・ステガノグラフィーの解読は、この文字列・メタデータ検査の対象外です。

## 検査用環境

Python 3.10以降が必要です。今回の検証には、Build内に独立配置したPython 3.12.10とUnityPy 1.25.2、pyelftools 0.32を使用しました。通常のPython仮想環境へ依存を入れて実行することもできます。ゲームのAssetsへは配置しません。

SMOL-Vは [aras-p/smol-v](https://github.com/aras-p/smol-v) の `source/smolv.cpp` と `source/smolv.h`、DXC APIは [Microsoftのdxcapi.h](https://github.com/microsoft/DirectXShaderCompiler/blob/main/include/dxc/dxcapi.h) を使用します。今回の取得元のGit blob SHAは次のとおりです。

| ファイル | Git blob SHA |
|---|---|
| smolv.cpp | `65068fd110c0dfa9acb3f581b330b5ef2e8e7eb8` |
| smolv.h | `231dfc6f307c3e691d7ced31bad4eb37605df418` |
| dxcapi.h | `d4cdc69c6ed088464d6f8ded597e1a6336168ae5` |

`Build-ContentAuditNative.ps1 -SmolvSourceDirectory <smolvのsourceフォルダー> -DxcIncludeDirectory <dxcapi.hのフォルダー> -OutDirectory <Build下の出力先>` で、検査用DLLとDXIL逆アセンブル用EXEを作ります。DXILの読み取りには、プロジェクトで使うUnity Editorの `Data/Tools/dxcompiler.dll` を指定します。元のデコーダーを変更せず、完全な復元と命令列の検証ができたSMOL-Vの範囲だけを採用します。

## 実行

```text
python Tools/AndroidFlat/Audit-DistributionContent.py
  --apk <検査済みAPK>
  --build-log <そのAPKの元のUnityビルドログ>
  --previous-inventory <既存の素材監査JSON>
  --record-directory <LogsまたはBuild下の記録先>
  --smolv-library <smolv-audit.dll>
  --dxil-adapter <dxil-audit.exe>
  --dxc-library <Unityのdxcompiler.dll>
  --staging <配布一式のローカルフォルダー>
```

引数は同じコマンドに続けます。仮想環境以外へ依存を配置した場合は `--modules <依存モジュールのフォルダー>` も指定できます。

`deep-content-inspection.json` に検査範囲・件数・検出数・元APKのSHA-256・検査器とデコーダーの指紋を記録します。`asset-inventory.json` は元のビルドログから復元した一覧です。後から別プラットフォームのビルドで上書きされたBuildReportを、対象APKの素材一覧として扱いません。
