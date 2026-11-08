# Windows 起動アプリ

`Build-Launcher.ps1 -OutputDirectory <Playerのフォルダー>` は Windows 付属の .NET Framework コンパイラーで `Zantetsu.exe` と `起動設定.exe` を作ります。Unity、NuGet、外部ランタイムの導入は不要です。`CutWorldSandbox.exe` は同じフォルダーに置きます。

通常は画面を出さず1回だけPlayerを起動し、終了コードをそのまま返します。`起動設定.exe`（`Zantetsu.exe --settings` と同じ処理）はXRを初期化しない日本語のデスクトップ設定です。2つのexeは同じソースから入口だけを変えて作り、移動後も自分のフォルダーのPlayerを起動します。絶対パスのショートカットは生成しません。保存先と形式は `Assets/Zantetsu/Runtime/Core/WindowsGraphicsPreference.cs` をそのままコンパイルして共有します。

- 明示した `-force-d3d11` / `-force-d3d12` が保存設定より優先します。両方の指定は拒否します。
- Vulkan、OpenGL、GLES、Metal、旧Direct3Dなど、同梱しない別APIの強制引数は説明して拒否します。別API指定にD3D指定を重ねて実行しません。
- 自動の互換対象は、単一の AMD RX 570（PCI `1002:67DF`）、ドライバー `31.0.21925.1001`、登録済み Meta/Oculus OpenXRランタイムの組合せだけです。GPU列挙では、名前が `Meta Virtual Monitor` と大文字小文字を除いて完全一致し、PNP IDが `ROOT\DISPLAY\` で始まるレコードだけを、候補数の判定より前に除外します。不明なレコードと、名前・PNP IDの片方しか一致しないレコードは候補に残します。GPUの候補が複数、不明、1.5秒で取得不能、別のXRランタイム指定がある場合は通常のD3D12です。これは既知構成の判定で、黒画面や接続中のHMDを検出する機能ではありません。
- D3D11には `-zantetsuVp3cUnityApi` を付けます。nativeの明示指定と矛盾する場合は説明して起動を拒否します。ただしUnity APIも明示されている場合は既存と同じくそちらが優先されます。
- `--settings` 以外の引数は順序・値を維持します。APIやUnity経路の引数が必要なら末尾へ追加します。シェルを介さずPlayerを起動します。
- MSAAはランチャーで変更しません。配布Player側のMSAA4設定を使います。APIとサンプル数は製品の起動記録で確認します。
- 選択理由は子プロセスの `ZANTETSU_GRAPHICS_SOURCE` / `ZANTETSU_GRAPHICS_REASON` と設定フォルダーの `launcher.log` に記録します。Playerの終了後に再起動はしません。

`Test-Launcher.ps1` は新しい一時フォルダーへ偽の `CutWorldSandbox.exe` を作り、保存・読込み、条件と優先順位、実際のWindows引数受け渡し（空文字、引用符、末尾のバックスラッシュ、日本語、空白入りパス）、終了コード、別フォルダーへコピー後の起動を確認します。本物のPlayer、Unity、XRは起動しません。設定UIの実画面と実GPUの検出は、この試験の範囲外です。
