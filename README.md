# ClaudeNote

OneNote の手書きノートを Claude に読ませて、応答をノートに書き戻す常駐ツール。

OneNote 上で範囲を選択してホットキー(既定: `Ctrl+Alt+A`)を押すと:

1. 選択中の手書き(ink)・画像を、内部データ(ISF バイナリ)から**透明 PNG** にレンダリング
2. **Claude Agent SDK**(常駐 Node サイドカー)経由で Claude に送信(**Claude Code のサブスク認証をそのまま利用**、API キー不要)
3. Claude の応答テキストを、**選択範囲の真下**に色付きテキストとして挿入

数学の途中式チェックや図の添削など、手書き学習ノートのフィードバック用。

**会話はセクション単位で継続する。** OneNote の同じセクションから送った内容は同じ claude セッションに積み上がるので、「さっきの続き」「前回の宿題を踏まえて」が通じる家庭教師になる。既存の Claude Code セッション(対話で使っていた学習セッションなど)に紐付けることもでき、その場合は対話 CLI とノート経由が**同一の会話を共有**する。

## 動作要件

- Windows + **デスクトップ版 OneNote** (Microsoft 365)。OneNote for Windows 10 (UWP) や Web 版は不可
- .NET 9 SDK (ビルド時)、Node.js 18+
- claude CLI (`npm i -g @anthropic-ai/claude-code`) でログイン済みであること

## ビルドと起動

```powershell
cd sidecar; npm install; cd ..
dotnet build src/ClaudeNote/ClaudeNote.csproj -c Release
& "src\ClaudeNote\bin\Release\net9.0-windows\ClaudeNote.exe"
```

実行中は exe がロックされるため、再ビルド時はトレイの「終了」で止めてから。

起動するとタスクトレイに常駐します。OneNote でなげなわ選択やドラッグで範囲を選び、`Ctrl+Alt+A` を押してください。応答まで数秒〜十数秒かかります(トレイのバルーンで通知)。

スタートアップ登録したい場合は `shell:startup` フォルダに exe のショートカットを置いてください。

## 設定

編集するファイルは 1 つだけ:

```
%LOCALAPPDATA%\ClaudeNote\appsettings.json
```

トレイアイコン右クリック →**「設定ファイルを開く」**で開ける。初回起動時に
`appsettings.sample.json`(リポジトリ同梱のテンプレート)からここへコピーされる。
以後 sample を編集しても動作には影響しない — 個人のパスやプロンプトがリポジトリに
入らないための分離。編集後はトレイの「終了」→ 再起動で反映される。

| キー | 説明 |
|---|---|
| `hotkey` | 例 `Ctrl+Alt+A`、`Ctrl+Shift+Q`。Ctrl/Alt/Shift/Win + キー |
| `model` | claude CLI に渡すモデル名。`null` で既定モデル |
| `claudePath` | claude CLI のフルパス。`null` なら PATH から探す |
| `timeoutSeconds` | Claude 応答のタイムアウト |
| `responseColor` | 挿入テキストの色 (CSS hex)。既定は紺色 `#1F4E79` |
| `keepArtifacts` | キャプチャ PNG と応答を `%LOCALAPPDATA%\ClaudeNote\workspace\captures` に残す |
| `sessionScope` | 会話継続の単位。`section` (既定) / `page` / `off` (毎回新規) |
| `workspaceDir` | Claude の作業ディレクトリ。`null` で `%LOCALAPPDATA%\ClaudeNote\workspace` |
| `engine` | `sdk` (Agent SDK サイドカー、既定) / `cli` (claude -p フォールバック) |
| `addDirs` | 作業ディレクトリ外で読み取りを許可するフォルダ。既定は Downloads / Documents / Videos / Pictures / Desktop。環境変数展開可 |
| `allowedTools` | Claude に自動許可するツール。既定はシェル実行込み (`Read, Glob, Grep, Bash, PowerShell, Write, Edit`)。読み取り専用に絞るなら `["Read","Glob","Grep"]` |
| `floatButton` | 画面右下の丸ボタンを表示 (既定 true)。タップでホットキーと同じ動作。ペン/タッチ用 |
| `floatButtonSize` | ボタンの直径 (論理px、既定 56。モニタの DPI に追従) |
| `profiles` | **セクション名で設定を切り替えるプロファイル** (下記) |

フローティングボタンはフォーカスを奪わない (`WS_EX_NOACTIVATE`) ため、OneNote の
選択状態を保ったままペンでタップできる。処理中はスパークが回転する。
`%LOCALAPPDATA%\ClaudeNote\button.png` を置くと既定のスパークの代わりにその画像が使われる。

## 図を描く (画像 / インク)

Claude の応答に次のディレクティブを書くと、その位置に図が挿入される:

```
{{image: C:\path\to\figure.png | width=200}}      画像を挿入 (width は pt、省略可)
{{ink: 0,0 100,0 100,60 | color=#1F4E79 | width=2}}   折れ線を1本描く
{{ink-overlay: 20,20 120,90 | color=#D40000}}     送った画像の座標のまま元のノートに重ねて描く
```

- `ink` の座標は **Claude に送ったキャプチャ画像のピクセル座標系**。`ink-overlay` は
  その座標をページ座標に逆変換して元の選択範囲に重ねるので、**子が描いた図の上に
  赤ペンで補助線を引く**ような添削ができる (実測誤差 0.5pt 未満)
- `ink` の連続行はまとめて 1 つの `one:InkDrawing` になる。挿入されるのは本物の
  インクなので、あとからペンや消しゴムで普通に編集できる
- 正確な作図 (角度・円) は Claude 自身がスクリプトで PNG を生成して `{{image:}}` で貼る
- 説明文はプロンプトの `{figureGuide}` に展開される (文面は設定で差し替え可)

`--figure-test` で挿入と座標変換を検証できる。

## プロファイル (セクションごとの設定切り替え)

OneNote のセクション名にワイルドカードでマッチさせ、一致したプロファイルの項目だけが
グローバル設定を上書きする (上から順に最初の一致が適用):

```json
"profiles": [
  {
    "match": "数学*",
    "workspaceDir": "C:\\path\\to\\my-tutor-repo",
    "promptTemplate": [ "..." ]
  }
]
```

上書きできる項目: `workspaceDir` / `model` / `addDirs` / `allowedTools` /
`promptTemplate` / `resumePromptTemplate` / `textOnlyPromptTemplate`。
作業ディレクトリを別リポジトリに向けると、そのリポジトリの CLAUDE.md が自動で
読み込まれるため、「セクションごとに別人格・別手順の Claude」を作れる。
会話セッションは元々セクション単位なので、プロファイルと自然に対応する。
| `sidecarDir` | `sidecar/index.mjs` の場所。`null` なら exe から上に辿って自動検出 |
| `nodePath` | node のパス。`null` なら PATH から |
| `promptTemplate` | 新規会話の最初のプロンプト (行の配列)。`{image}` `{textSection}` が置換される |
| `resumePromptTemplate` | 会話継続時の短いプロンプト。文脈はセッション側にある前提 |
| `textOnlyPromptTemplate` | テキストのみ選択時のプロンプト。`{text}` が置換される |

## 会話セッションの仕組み

- 対応表は `%LOCALAPPDATA%\ClaudeNote\sessions.json` (セクション/ページ ID → claude セッション ID)
- 初回は新規会話を開始し、返ってきた session_id を保存。以降は `--resume` で継続
- 保存していたセッションが消えていた場合は自動で新規会話にフォールバック
- トレイメニュー「会話セッションをリセット」で全対応を破棄 (次回から新規会話)
- **既存の Claude Code セッションに接続するには**: `claude --resume` 一覧などでセッション ID を調べ、
  `sessions.json` に手動でエントリを書く。キーは OneNote のセクション ID
  (このリポジトリの `--capture-test` 実行時のログや、階層 XML から確認できる)

プロンプトを変えれば「答えを言わずヒントだけ」「採点して」「英訳して」など用途を変えられる。

## 動作検証コマンド

```powershell
ClaudeNote.exe --capture-test               # いま OneNote で選択中の内容を PNG 化のみ (挿入なし)
ClaudeNote.exe --render-test <xml> <png>    # 保存済みページ XML の全 ink を PNG 化
ClaudeNote.exe --ask-test <png> [sessionId] # PNG を Claude に送って応答を表示のみ (sessionId 指定で resume 検証)
ClaudeNote.exe --insert-test                # テストページ作成→挿入→検証→削除
ClaudeNote.exe --figure-test                # 図 (画像+インク+補助線) の挿入と座標変換を検証
```

ログ: `%LOCALAPPDATA%\ClaudeNote\claude-note.log`

## 仕組み

```
[ホットキー] → OneNote COM API (GetPageContent, piBinaryDataSelection)
            → ページ XML から selected="all|partial" の要素を抽出
               ├ one:InkDrawing / one:InkWord … base64 ISF → WPF StrokeCollection
               ├ one:Image … base64 画像
               └ one:T … テキスト (プロンプトに添付)
            → one:Position (pt 座標) に基づき合成、透明 PNG (約192dpi)
            → Claude Agent SDK (常駐 node サイドカー。allowedTools は設定で制御、
               既定はシェル実行込み。additionalDirectories=addDirs で cwd 外の資料も読める)
            → 応答を解析し、テキストは one:Outline、{{image:}} は one:Image、
               {{ink:}} は折れ線→ISF 変換して one:InkDrawing として
               選択範囲の真下に UpdatePageContent で挿入
```

サイドカー (`sidecar/index.mjs`) は stdin/stdout の JSON Lines で C# 側と通信する常駐
Node プロセス。Claude Agent SDK の `query()` に `resume` / `additionalDirectories` /
`allowedTools` を明示的に渡すので、`claude -p` の暗黙の権限規則 (cwd 外は読めない等)
に依存しない。エラー時は C# 側がプロセスを再起動する。

実装上の注意点(ハマりどころ):

- OneNote の IDispatch は型情報取得に失敗するため、.NET の `dynamic` や
  `Type.InvokeMember` 遅延バインディングは **使えない**。GAC の PIA
  (`Microsoft.Office.Interop.OneNote`) をロードし `Application2Class` を
  リフレクションで呼んでいる (`OneNoteApp.cs`)
- GAC には旧バージョン (v12) の PIA が残っていることがあり、新しい順に選ぶ必要がある
- `GetHierarchy` / `GetPageContent` はスキーマ既定値が古いため `xs2013` を明示指定する
- 手書きはストローク断片ごとに `one:InkDrawing` として保存されており、
  各要素の `one:Position`/`one:Size` (pt) で再配置して合成する

## 既知の制限

- アウトライン内に変換された手書きテキスト (InkWord) は位置情報を持たないため、
  キャプチャ画像の末尾にまとめて描画される (通常の自由手書きは影響なし)
- 応答の挿入位置は「選択範囲の外接矩形の真下」。既存要素と重なる場合がある
- 挿入されるのはプレーンテキストのみ (数式レンダリングなどはなし)
