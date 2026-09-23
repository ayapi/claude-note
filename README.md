# ClaudeNote

OneNote の手書きノートを Claude に読ませて、応答をノートに書き戻す常駐ツール。

ノートに書いてホットキー(既定: `Ctrl+Alt+A`)を押すと:

1. **前回送ってから新しく書かれたぶん**を自動で見つけ、手書き(ink)・画像を
   内部データ(ISF バイナリ)から **PNG** にレンダリング
   (背景は既定で自動選択。透明にすると表示側の合成色しだいで黒インクが読めなくなる)
2. **Claude Agent SDK**(常駐 Node サイドカー)経由で Claude に送信(**Claude Code のサブスク認証をそのまま利用**、API キー不要)
3. Claude の応答テキストを、ページ下端(既定)に色付きテキストとして挿入

**範囲選択は不要。** ペンで書いてボタンを押すだけで、書いたぶんだけが送られる
(ペンと選択モードを切り替える手間をなくすため)。仕組みは[下記](#送る範囲の決め方-差分)。

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

起動するとタスクトレイに常駐します。OneNote に書いてから `Ctrl+Alt+A` を押してください。応答まで数秒〜十数秒かかります(トレイのバルーンで通知)。

スタートアップ登録したい場合は `shell:startup` フォルダに exe のショートカットを置いてください。

## 会話セッションの区切り方

`sessionScope` で、どの単位で claude の会話を続けるかを決めます。

- `page` (既定) — OneNote のページを新しくするたびに会話を作り直します。会話が短く保たれます
- `section` — 同じセクションならずっと同じ会話。文脈が積み上がる代わりに、
  使い込むほど 1 往復の入力が膨らみ、応答が遅く高くなります。会話が長くなりすぎると
  精度も落ちます (実績: 8/9〜9/5 の算数セクションで 1 往復 46K → 296K トークン)
- `off` — 毎回まっさらな会話

`page` にすると話の流れが切れてしまうため、`sessionHandoff`(既定で有効)を併せて使います。
新しいページで会話を作り直す直前に、**前のページの会話へ申し送りを書かせて**、新しい会話の
冒頭に渡します。何に取り組みどこまで進んだか、どこでつまずいたか、次に何をするつもりだったかが
引き継がれます。余分な問い合わせはページ 1 枚につき 1 回だけです。

文面は `handoffSummaryPrompt`(書かせる側)と `handoffPromptTemplate`(渡す側)で変えられます。
`section` から `page` に切り替えた直後は、それまで使っていたセクションの会話を 1 度だけ
引き継ぎ元にするので、積み上げた文脈が捨てられることはありません。

## 更新

トレイアイコン右クリック →**「更新を確認して適用」**。リモートとの差分を調べ、取り込む
コミットの一覧を見せてから、取得 → `npm install` → ビルド → 再起動までを行います。
ターミナルを開く必要はありません。

実行中は exe がロックされていてビルドできないため、実際の作業は ClaudeNote を終了させて
から外部スクリプト (`%LOCALAPPDATA%\ClaudeNote\update.ps1`) が引き継ぎます。作業中は
進行が見えるようコンソールが表示されます。

途中で失敗した場合は取り込みごと更新前の位置に巻き戻し、元の exe を起動し直します
(ソースだけ新しくてバイナリが古い、という食い違いを残さないため)。経過は
`%LOCALAPPDATA%\ClaudeNote\update.log` に残ります。

未コミットの変更があるリポジトリでは実行しません。コマンドラインからは
`ClaudeNote.exe --update-check` で確認だけ、`--update-apply` で適用まで行えます。

### 一緒に走らせる自前のコマンド (updateHooks)

`updateHooks` に書いたコマンドが「更新を確認して適用」のたびに実行されます。
教材リポジトリなど、ClaudeNote 本体とは別に更新したいものを同じ操作で済ませるための
仕組みです。**ClaudeNote 本体に更新が無くても実行されます** (別リポジトリの更新が
目的なので、本体が最新でも取り込みたいため)。

```json
"updateHooks": [
  {
    "name": "ait を更新",
    "command": "git pull --ff-only",
    "workingDir": "%USERPROFILE%\\code\\ait",
    "timeoutSeconds": 120,
    "enabled": true
  }
]
```

| キー | 意味 |
|---|---|
| `name` | ダイアログとログに出す名前。省略するとコマンドがそのまま使われる |
| `command` | PowerShell に渡される 1 行。複数コマンドは `;` でつなぐ |
| `workingDir` | 実行するディレクトリ。環境変数を展開する。省略可 |
| `timeoutSeconds` | これを過ぎたらプロセスごと打ち切る (既定 120、最小 5) |
| `enabled` | `false` で設定を消さずに止められる |

上から順に実行し、1 つ失敗しても残りは実行します。結果は更新ダイアログの先頭に
`○` / `×` で出て、`claude-note.log` にも残ります。本体の更新の取り込み自体は、
フックが失敗しても止まりません。

`ClaudeNote.exe --update-hooks` でフックだけを実行できます (本体の取り込み・ビルドはしない)。

> 設定ファイルの JSON を書き間違えると、**すべて既定値で起動します**
> (`workspaceDir` もプロファイルも API キーも消える)。気づけるよう起動時に警告を
> 出しますが、Windows のパスを書くときは `\\` の数に注意してください。

## 設定

編集するファイルは 1 つだけ:

```
%LOCALAPPDATA%\ClaudeNote\appsettings.json
```

トレイアイコン右クリック →**「設定ファイルを開く」**で開ける。初回起動時に
`appsettings.sample.json`(リポジトリ同梱のテンプレート)からここへコピーされる。
以後 sample を編集しても動作には影響しない — 個人のパスやプロンプトがリポジトリに
入らないための分離。

**編集は再起動なしで反映される。** 実行のたびにファイルの更新時刻を見て、変わって
いれば読み直す (プロンプトを調整しながら試せる)。書きかけで JSON が壊れている場合は
直前に成功した設定を使い続け、通知で知らせる (既定値に戻ってしまうことはない)。

ただし次の項目は起動時にしか適用できないため、変更したら再起動が必要
(変更を検知すると通知する): `hotkey` / `floatButton` / `floatButtonSize` /
`nodePath` / `sidecarDir`。

| キー | 説明 |
|---|---|
| `hotkey` | 例 `Ctrl+Alt+A`、`Ctrl+Shift+Q`。Ctrl/Alt/Shift/Win + キー |
| `model` | claude CLI に渡すモデル名。`null` で既定モデル |
| `claudePath` | claude CLI のフルパス。`null` なら PATH から探す |
| `timeoutSeconds` | Claude 応答のタイムアウト |
| `responseColor` | 挿入テキストの色 (CSS hex)。既定は紺色 `#1F4E79` |
| `responseWidthChars` | 応答テキストの横幅 (全角の文字数、既定 35)。0 以下で書かれた部分の幅に合わせる |
| `responseCharWidthPt` | 全角 1 文字ぶんの幅 (pt、既定 11)。OneNote の本文フォントのサイズと同じ値にする |
| `captureBackground` | 送信する画像の背景。`auto` (既定) はインクの明るさで白/暗色を選ぶ。`white` / `black` / `transparent` / `#RRGGBB` も可 |
| `includeOverlapping` | 書いた場所に重なる古い図も一緒に送る (既定 true、[下記](#書いた場所に重なるものも送る)) |
| `overlapMarginPt` | 重なり判定を広げる余白 (pt、既定 8) |
| `overlapMaxObjects` | 巻き込みで送る上限個数 (既定 600) |
| `insertPosition` | 回答の挿入位置。`belowAll` (既定) はページ全体の下端 (空白部分)、`belowWriting` は今回書かれた部分の真下。x 座標はどちらも書かれた部分の左端に揃う |
| `keepArtifacts` | キャプチャ PNG と応答を `%LOCALAPPDATA%\ClaudeNote\workspace\captures` に残す |
| `sessionScope` | 会話継続の単位。`page` (既定) / `section` / `off` (毎回新規) |
| `workspaceDir` | Claude の作業ディレクトリ。`null` で `%LOCALAPPDATA%\ClaudeNote\workspace` |
| `engine` | `sdk` (Agent SDK サイドカー、既定) / `cli` (claude -p フォールバック) |
| `addDirs` | 作業ディレクトリ外で読み取りを許可するフォルダ。既定は Downloads / Documents / Videos / Pictures / Desktop。環境変数展開可 |
| `allowedTools` | Claude に自動許可するツール。既定はシェル実行込み (`Read, Glob, Grep, Bash, PowerShell, Write, Edit`)。読み取り専用に絞るなら `["Read","Glob","Grep"]` |
| `floatButton` | 画面右下の丸ボタンを表示 (既定 true)。タップでホットキーと同じ動作。ペン/タッチ用 |
| `floatButtonSize` | ボタンの直径 (論理px、既定 56。モニタの DPI に追従) |
| `updateHooks` | **「更新を確認して適用」で一緒に走らせる自前のコマンド** (上記) |
| `profiles` | **セクション名で設定を切り替えるプロファイル** (下記) |

フローティングボタンはフォーカスを奪わない (`WS_EX_NOACTIVATE`) ため、OneNote の
OneNote の状態を保ったままペンでタップできる。

- **OneNote が前面のときだけ表示される** (`EVENT_SYSTEM_FOREGROUND` のフックで追従)。
  他のアプリを使っている間は邪魔にならない
- **処理中はスパークが回転**し、カーソルを乗せると×印 (中断) に変わる。この状態で押すと
  **実行中の問い合わせをキャンセル**できる (ホットキーの再押下でも同じ)。
  キャンセルするとノートには何も挿入されず、会話セッションも更新されない
- ボタンにカーソルを乗せると、いまの状態と押したときの動作が吹き出しで出る
  (待機中「タップ: 書いたものを送る / 長押し: 音声で質問」、録音中「離すと送ります」、
  処理中「押すと中断します」)
- **長押しで音声入力** (下記)。押している間だけ録音し、離すと文字起こしされる
- `%LOCALAPPDATA%\ClaudeNote\button.png` を置くと既定のスパークの代わりにその画像が使われる

## 音声入力

丸ボタンを**押している間**がマイク録音になる (既定 400ms 以上で長押し判定、録音中は赤く明滅)。
指を離すと次の順で処理される:

1. 文字起こし
2. **文字起こしを先にノートへ挿入** (行頭に 💬、灰色)。認識が合っているかすぐ確認できる
3. 文字起こし + **新しく書かれた部分のキャプチャ画像**を Claude に送信
4. 回答を吹き出しの真下に挿入

新しく書かれたものがあれば画像も一緒に送るので、図を描いて「これの面積はどう求めるの?」と
口で聞ける (`voiceIncludesWriting` で無効化可)。

文字起こしエンジンは環境に合わせて選べる:

| `sttEngine` | 内容 |
|---|---|
| `auto` (既定) | whisper → openai → windows の順に、使えるものを選ぶ (失敗したら次で試し直す) |
| `whisper` | whisper.cpp。高精度でローカル完結だがモデル (`ggml-*.bin`) の配置が必要。`whisperExe` / `whisperModel` を設定する |
| `openai` | OpenAI の文字起こし API (既定 `gpt-4o-transcribe`)。高精度だが `openaiApiKey` (または環境変数 `OPENAI_API_KEY`) と通信が必要で従量課金 |
| `windows` | Windows 標準の音声認識 (System.Speech)。追加インストール不要で速いが精度は劣る |

実測 (8秒の音声): whisper large-v3-turbo は約 6 秒で高精度、Windows 標準は約 0.7 秒だが
誤認識が目立つ。短い質問なら whisper の小さめモデル (base / small) でも足りる。

録音は**ボタンに触れた瞬間**に始める (長押しの判定を待たない)。マイクは起動に 1 秒近く
かかるため、判定後に開くと話し始めが切れて「一割合」「開始了」のような結果になる。
長押しにならずタップで終わった録音は捨てる。

文字起こしの前に `AudioCleaner` で整形する: 先頭のマイク起動待ち (ゼロ詰め) と前後の
無音を切り落とし、内蔵マイクの小さな音量を持ち上げ (最大 30 倍)、前後に 0.3 秒の無音を
足す。整形後の音声が 0.3 秒未満なら送らない (どのエンジンも無音に対して幻聴を起こすため)。
整形前の `voice.wav` と整形後の `voice.clean.wav` は両方 captures フォルダに残る。

さらに `sttPrompt` (既定は「生徒が手書きのノートについて質問しています…」) を
ヒントとして渡す。短い発話だと `language: ja` だけでは中国語や英語に化けることが
あり、同じ言語の文を先に見せることで引き戻す。よく出る専門用語を含めておくと
その語が拾われやすい。

**ペン・タッチ対応**: Windows は既定でペン/タッチの長押しを「右クリック」ジェスチャに
変換するため、そのままでは左ボタンが押しっぱなしにならず長押しが成立しない。
`WM_TABLET_QUERYSYSTEMGESTURESTATUS` に応答して押し続けジェスチャとフリックを
無効化している (`FloatButtonForm.WndProc`)。うまく反応しないときはログの
`ボタン MouseDown` 行を見ると、入力がマウスかペン/タッチか、どのボタンとして
届いたかが分かる。

## 図を描く (画像 / インク)

Claude の応答に次のディレクティブを書くと、その位置に図が挿入される:

```
{{image: C:\path\to\figure.png | width=200}}      画像を挿入 (width は pt、省略可)
{{ink: 0,0 100,0 100,60 | color=#1F4E79 | width=2}}   折れ線を1本描く
{{ink-overlay: 20,20 120,90 | color=#D40000}}     送った画像の座標のまま元のノートに重ねて描く
{{ink-overlay: circle 300,400 r=25 | color=#D40000 | width=3}}   円 (中心と半径)
{{ink-overlay: wave 100,300 260,300 | color=#D40000 | width=2}}  波線 (2点を結ぶ。amp=4 で振幅)
{{ink-overlay: ? 280,285 size=20 | color=#D40000 | width=2}}     「?」 (x,y は記号の上端中央)
```

`circle` / `wave` / `?` は点列の省略記法。`r=` `amp=` `size=` は第1区画にも
`| amp=8` の形にも書ける (モデルがどちらで書くか定まらないため両方受ける)。

- `ink` の座標は **Claude に送ったキャプチャ画像のピクセル座標系**。`ink-overlay` は
  その座標をページ座標に逆変換して元の位置に重ねるので、**子が描いた図の上に
  赤ペンで補助線を引く**ような添削ができる (実測誤差 0.5pt 未満)
- `ink` の連続行はまとめて 1 つの `one:InkDrawing` になる。挿入されるのは本物の
  インクなので、あとからペンや消しゴムで普通に編集できる
- **丸付けの流儀**を `figureGuide` で指示している: 正解は答えを赤い○で囲む / 間違いのときは
  **答えには何も付けず**、怪しい途中式の行に波線を引いて行末に `?` を置く / ×・レ点・斜線は
  使わない (どこがどう違うかは本文の言葉で説明する)。折れ線だけだとこれらを描くのに
  20〜30 点並べることになり、モデルが面倒がって緑の ✓ 一発で済ませてしまったため、
  省略記法を用意した
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
| `textOnlyPromptTemplate` | 手書きが無くテキストだけのときのプロンプト。`{text}` が置換される |

## 会話セッションの仕組み

- 対応表は `%LOCALAPPDATA%\ClaudeNote\sessions.json` (セクション/ページ ID → claude セッション ID)
- 初回は新規会話を開始し、返ってきた session_id を保存。以降は `--resume` で継続
- **継続に失敗したときは、前のセッションの記録を読ませて引き継がせる**。
  Claude Code は会話を `~/.claude/projects/<project>/<id>.jsonl` に残しているので、
  新しいセッションの冒頭でそのファイルを読ませて文脈を復元する
  (`sessionTakeover: false` で無効化可、指示文は `sessionTakeoverPromptTemplate`)。
  記録ファイルも見つからない場合のみ、文脈なしの新規会話になる
- 通知には実際にどうなったかが出る (「会話の続き」「前セッションを引き継ぎ」
  「新規会話」「新規会話 (文脈なし)」)
- トレイメニュー「会話セッションをリセット」で全対応を破棄 (次回から新規会話)
- **既存の Claude Code セッションに接続するには**: `claude --resume` 一覧などでセッション ID を調べ、
  `sessions.json` に手動でエントリを書く。キーは OneNote のセクション ID
  (このリポジトリの `--capture-test` 実行時のログや、階層 XML から確認できる)

プロンプトを変えれば「答えを言わずヒントだけ」「採点して」「英訳して」など用途を変えられる。

## 動作検証コマンド

```powershell
ClaudeNote.exe --capture-test               # 前回から書かれたぶんを PNG 化のみ (挿入なし)
ClaudeNote.exe --diff-test                  # 差分の検出を対話で試す (Enter = 送るボタン相当)
ClaudeNote.exe --render-test <xml> <png>    # 保存済みページ XML の全 ink を PNG 化
ClaudeNote.exe --ask-test <png> [sessionId] # PNG を Claude に送って応答を表示のみ (sessionId 指定で resume 検証)
ClaudeNote.exe --insert-test                # テストページ作成→挿入→検証→削除
ClaudeNote.exe --figure-test                # 図 (画像+インク+補助線) の挿入と座標変換を検証
ClaudeNote.exe --mic-list                   # 録音デバイスの一覧
ClaudeNote.exe --record-test [秒]           # 録音して文字起こしまで通す
ClaudeNote.exe --stt-test <wav> [engine]    # 既存の WAV を文字起こし (engine 指定で比較できる)
ClaudeNote.exe --voice-insert-test          # 吹き出し → 回答の2段階挿入を検証
ClaudeNote.exe --multipart-test             # テキスト+画像が混ざった応答が重ならないか検証
ClaudeNote.exe --cancel-test                # 実行→キャンセル→続けて次を実行、が通るか検証
```

※ この exe は WinExe のため、コンソールから実行するときは `| Out-String` などで
パイプしないと出力が表示されない。

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
               ページ下端に UpdatePageContent で挿入
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

## 送る範囲の決め方 (差分)

範囲選択はさせない。**前回送ったときからページがどう変わったか**を見て、
増えたぶんだけを送る。

OneNote のページ XML は、手書きのストローク (`one:InkDrawing`) ごと・段落
(`one:OE`) ごとに **`objectID` と `lastModifiedTime`** を持つ。前回の姿を
objectID → 指紋 (手書きは位置と大きさ、段落は本文のハッシュ) で覚えておき、
突き合わせれば増えたものが分かる。画像処理は要らない。

```
1. 軽い XML (バイナリ抜き) を取得してスナップショットを作る
2. 保存してある基準と突き合わせ、増えた / 変わった objectID を出す
3. その objectID のぶんだけ ISF 込みで取り出して PNG 化
4. 応答を挿入したあと、改めてスナップショットを取り直して基準を更新
```

4 が重要で、**Claude 自身が書いた応答や赤ペンの添削を基準に含める**ことで、
次に送るときの差分から外れる。

基準はページごとに `%LOCALAPPDATA%\ClaudeNote\baselines.json` に置く
(手編集する `sessions.json` に数百個の objectID を混ぜないため。50 ページ分まで保持)。

### タイトルから始める

差分が無いときは、通常なら「まだ何も書かれていません」で終わる。ただし
**本文が空でタイトルだけ書かれている**ページなら、そのタイトルを「やりたいこと」
として扱い、`topicStartPromptTemplate` を送る (`{title}` にタイトルが入る)。

新しいページを開いてタイトルに単元名を書き、ボタンを押すだけで始められる。
何も書いていない状態から始めるのに、わざわざ音声で言う必要がない。

| ページの状態 | 動き |
| --- | --- |
| 差分あり | 書かれたぶんを送る (通常) |
| 差分なし・本文が空・タイトルあり | タイトルの内容で始める |
| 差分なし・本文が空・タイトルなし | 何もしない (タイトルを書くよう促す) |
| 差分なし・本文あり | 何もしない (書いてから押すよう促す) |

一度応答が入ると本文が空でなくなるので、続けて押しても再開始はしない。
`ClaudeNote.exe --topic-test` で、いま開いているページがどの行に当たるか
(と送られるプロンプト) を、送信せずに確認できる。

画素を比べる方法もあるが、こちらのほうが:

- 表示の拡大率やスクロール位置に影響されない
- 座標が pt 単位で得られ、そのまま `ink-overlay` の座標系になる
- 前に書いたものと重なる位置に書いても正しく分離できる
- 軽い (191 ストロークのページで 42KB の XML)

実測 (手書き 91 → 181 ストロークのページ):

| | |
|---|---|
| 併合 (objectID 据え置きで矩形だけ拡大) | 7 回の書き込みで 0 回。ストロークは毎回新しい objectID で足される |
| 取得と突き合わせ | `--bench-capture` で段階ごとに計測できる |

### 書いた場所に重なるものも送る

増えたぶんだけを送ると、**図形問題で図の上に補助線を引いたとき**に補助線しか
送られず、絵として意味が通らない。そこで、新しく書かれた範囲に重なる古い図や
手書きも一緒に送る (`includeOverlapping`、既定で有効)。

巻き込んだぶんで範囲が広がると、さらに別の図と重なることがあるので、変化が
無くなるまで繰り返す (`overlapMaxObjects` で打ち切り)。`overlapMarginPt` (既定 8pt)
だけ広げて判定するので、線のすぐ隣にある図も拾える。

段落は巻き込まない。文字はすでに会話の中にあり、絵として送り直す必要が無いため。

**基準が無いページ** (初めて送る、または会話をリセットした直後) では、
ページにあるものすべてが「新しく書かれたもの」になる。

切り貼りで移動させたものは objectID が変わるため、差分では「新しく書かれた」
扱いになる (1 回だけ広めに送られる)。

## 回答の横幅

応答テキストの横幅は既定で **全角 35 文字** (`responseWidthChars`) に固定する。
以前は送る範囲の幅をそのまま使っていたため、書いた大きさで 1 行の長さが
変わって読みにくかった。

幅は `responseWidthChars × responseCharWidthPt` (pt) で決まる。全角文字は
1em = フォントサイズぶんの幅なので、`responseCharWidthPt` には OneNote の本文
フォントのサイズを入れる (既定 11pt = 游ゴシック 11pt に対応)。

実測 (385pt の場合): 35 文字は 1 行に収まり、36 文字で折り返す。
`--width-test` で確認できる。

## 回答の挿入位置

既定 (`insertPosition: "belowAll"`) では、

- **x** は今回書かれた内容の左端に揃える (話の流れが縦に並ぶ)
- **y** はページ上の全要素の下端、つまり**まだ何も書かれていない空白部分**

とするため、既存の手書きや図と重なることが原理的に起きない。音声入力の吹き出しと
回答も同じ規則で置かれるので、吹き出し → 回答の順に下へ積まれる。

回答にテキストと図が混ざっている場合は、**1 つずつ挿入して そのつど実際の下端を
測り直す**。テキストの高さは折り返しによって変わり事前に見積もれないため、
まとめて挿入すると 2 つ目以降が少し上にずれて重なってしまう。

書いた部分の真下に置きたい場合は `insertPosition` を `belowWriting` にする
(ノートが長いと回答が画面外になるのを避けたいとき向け。ただしこの場合は実測できず
見積もりで一括挿入するため、重なる可能性がある)。

## 既知の制限

- アウトライン内に変換された手書きテキスト (InkWord) は位置情報を持たないため、
  キャプチャ画像の末尾にまとめて描画される (通常の自由手書きは影響なし)
- ノートが長い場合、回答はページ末尾に入るため書いた場所から離れた位置になる
- 挿入されるのはプレーンテキストのみ (数式レンダリングなどはなし)
