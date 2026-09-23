using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ClaudeNote;

/// <summary>
/// OneNote のセクション名で切り替える設定プロファイル。
/// 指定したフィールドだけがグローバル設定を上書きする (null = グローバルを使う)。
/// </summary>
public sealed class ConfigProfile
{
    /// <summary>セクション名のワイルドカードパターン (例: "FDE*", "数学?")。最初に一致したものが適用される。</summary>
    [JsonPropertyName("match")]
    public string Match { get; set; } = "*";

    [JsonPropertyName("workspaceDir")]
    public string? WorkspaceDir { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("addDirs")]
    public string[]? AddDirs { get; set; }

    [JsonPropertyName("allowedTools")]
    public string[]? AllowedTools { get; set; }

    [JsonPropertyName("promptTemplate")]
    public string[]? PromptTemplate { get; set; }

    [JsonPropertyName("resumePromptTemplate")]
    public string[]? ResumePromptTemplate { get; set; }

    [JsonPropertyName("textOnlyPromptTemplate")]
    public string[]? TextOnlyPromptTemplate { get; set; }

    [JsonPropertyName("voicePromptTemplate")]
    public string[]? VoicePromptTemplate { get; set; }

    [JsonPropertyName("topicStartPromptTemplate")]
    public string[]? TopicStartPromptTemplate { get; set; }

    /// <summary>会話の区切り方 (section / page / off)。用途ごとに変えたいので上書きできる。</summary>
    [JsonPropertyName("sessionScope")]
    public string? SessionScope { get; set; }

    [JsonPropertyName("handoffSummaryPrompt")]
    public string[]? HandoffSummaryPrompt { get; set; }

    [JsonPropertyName("handoffPromptTemplate")]
    public string[]? HandoffPromptTemplate { get; set; }
}

/// <summary>
/// 「更新を確認して適用」のときに一緒に走らせる自前のコマンド。
/// ClaudeNote 本体とは別のリポジトリ (教材や作業ディレクトリなど) を
/// 同じ操作で更新できるようにするためのもの。
/// ClaudeNote 自身に更新が無くても実行される。
/// </summary>
public sealed class UpdateHook
{
    /// <summary>ダイアログとログに出す名前。省略するとコマンドがそのまま使われる。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>PowerShell に渡すコマンド (例: "git pull --ff-only")。</summary>
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    /// <summary>実行するディレクトリ。環境変数を展開する。</summary>
    [JsonPropertyName("workingDir")]
    public string? WorkingDir { get; set; }

    /// <summary>この秒数で打ち切る。</summary>
    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>false にすると設定を消さずに止められる。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    public string Label => string.IsNullOrWhiteSpace(Name) ? Command : Name!;
}

public sealed class AppConfig
{
    [JsonPropertyName("hotkey")]
    public string Hotkey { get; set; } = "Ctrl+Alt+A";

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>claude CLI のフルパス。null なら PATH から探す。</summary>
    [JsonPropertyName("claudePath")]
    public string? ClaudePath { get; set; }

    /// <summary>sdk エンジンでは「無応答」タイムアウト (進行イベントが届くたびリセット)。
    /// cli エンジンでは実行全体のタイムアウト。</summary>
    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>挿入するテキストの色 (CSS hex)。</summary>
    [JsonPropertyName("responseColor")]
    public string ResponseColor { get; set; } = "#1F4E79";

    /// <summary>
    /// 応答テキストの横幅 (全角の文字数)。0 以下にすると書かれた部分の幅に合わせる
    /// (従来の挙動。書いた大きさで行長が変わって読みにくい)。
    /// </summary>
    [JsonPropertyName("responseWidthChars")]
    public int ResponseWidthChars { get; set; } = 35;

    /// <summary>
    /// 全角 1 文字ぶんの幅 (pt)。OneNote の本文フォントのサイズと同じ値にする
    /// (全角文字は 1em = フォントサイズぶんの幅を持つため)。既定の 11pt は
    /// OneNote の標準 (游ゴシック 11pt) に合わせたもの。
    /// </summary>
    [JsonPropertyName("responseCharWidthPt")]
    public double ResponseCharWidthPt { get; set; } = 11.0;

    /// <summary>応答テキストの横幅 (pt)。幅を指定しない設定なら null。</summary>
    public double? ResponseWidthPt =>
        ResponseWidthChars > 0 ? ResponseWidthChars * ResponseCharWidthPt : null;

    /// <summary>
    /// 新しく書かれた範囲に重なる、古い図や手書きも一緒に送るか。
    /// 図の上に補助線を引いたとき、補助線だけでは意味が通らないため既定で有効。
    /// </summary>
    [JsonPropertyName("includeOverlapping")]
    public bool IncludeOverlapping { get; set; } = true;

    /// <summary>重なり判定を何 pt ぶん広げて見るか。近くにある図も拾いたいとき用。</summary>
    [JsonPropertyName("overlapMarginPt")]
    public double OverlapMarginPt { get; set; } = 8;

    /// <summary>巻き込みで送るオブジェクト数の上限 (暴走を防ぐ)。</summary>
    [JsonPropertyName("overlapMaxObjects")]
    public int OverlapMaxObjects { get; set; } = 600;

    /// <summary>
    /// Claude に送るキャプチャ画像の背景。"auto" (既定) はインクの明るさから
    /// 白か暗色かを選ぶ。"white" / "black" / "transparent" / "#RRGGBB" も指定可。
    /// 透明にすると、表示側の合成色によっては黒インクが読めなくなる。
    /// </summary>
    [JsonPropertyName("captureBackground")]
    public string CaptureBackground { get; set; } = "auto";

    /// <summary>
    /// 回答の挿入位置。"belowAll" (既定) はページ全体の下端 (空白部分) に置くため
    /// 既存の内容と重ならない。"belowWriting" は今回書かれた部分の真下に置く。
    /// x 座標はどちらも書かれた部分の左端に揃える。
    /// </summary>
    [JsonPropertyName("insertPosition")]
    public string InsertPosition { get; set; } = "belowAll";

    public bool InsertBelowAll =>
        !InsertPosition.Equals("belowWriting", StringComparison.OrdinalIgnoreCase);

    /// <summary>キャプチャ PNG と応答を残すか。</summary>
    [JsonPropertyName("keepArtifacts")]
    public bool KeepArtifacts { get; set; } = true;

    /// <summary>
    /// 会話セッションの継続単位。"page" = OneNote のページごとに会話を作り直す (既定)、
    /// "section" = セクションごとに会話を継続、"off" = 毎回新規会話。
    /// section だと同じセクションで会話が延々と続き、1 往復あたりの入力が
    /// 46K → 296K トークンまで膨らんだ実績がある。page + SessionHandoff なら
    /// 申し送りで話の流れを保ったまま会話を短く保てる。
    /// </summary>
    [JsonPropertyName("sessionScope")]
    public string SessionScope { get; set; } = "page";

    /// <summary>claude CLI の作業ディレクトリ。null なら %LOCALAPPDATA%\ClaudeNote\workspace。</summary>
    [JsonPropertyName("workspaceDir")]
    public string? WorkspaceDir { get; set; }

    /// <summary>Claude 呼び出しエンジン。"sdk" (Agent SDK サイドカー、既定) または "cli" (claude -p)。</summary>
    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "sdk";

    /// <summary>node のパス。null なら PATH から探す。</summary>
    [JsonPropertyName("nodePath")]
    public string? NodePath { get; set; }

    /// <summary>sidecar/index.mjs のあるディレクトリ。null なら exe から上に辿って探す。</summary>
    [JsonPropertyName("sidecarDir")]
    public string? SidecarDir { get; set; }

    /// <summary>Claude に自動許可するツール。既定はシェル実行 (Bash/PowerShell) 込み。
    /// 読み取り専用に絞りたい場合は ["Read","Glob","Grep"] にする。</summary>
    [JsonPropertyName("allowedTools")]
    public string[] AllowedTools { get; set; } =
    [
        "Read", "Glob", "Grep", "Bash", "PowerShell", "Write", "Edit",
    ];

    /// <summary>作業ディレクトリ以外で Claude に読み取りを許可するフォルダ。環境変数展開可。</summary>
    [JsonPropertyName("addDirs")]
    public string[] AddDirs { get; set; } =
    [
        "%USERPROFILE%\\Downloads",
        "%USERPROFILE%\\Documents",
        "%USERPROFILE%\\Videos",
        "%USERPROFILE%\\Pictures",
        "%USERPROFILE%\\Desktop",
    ];

    public string[] ExpandedAddDirs =>
        AddDirs.Select(Environment.ExpandEnvironmentVariables)
               .Where(Directory.Exists)
               .ToArray();

    [JsonPropertyName("promptTemplate")]
    public string[] PromptTemplate { get; set; } =
    [
        "まず {image} を Read ツールで読み取ってください。これは OneNote の手書きノートから切り出した画像です。",
        "内容について日本語で簡潔に応答してください。出力はプレーンテキストのみ。",
        "{textSection}",
    ];

    [JsonPropertyName("textOnlyPromptTemplate")]
    public string[] TextOnlyPromptTemplate { get; set; } =
    [
        "以下は OneNote に新しく書かれたテキストです。日本語で簡潔に応答してください。出力はプレーンテキストのみ。",
        "---",
        "{text}",
        "---",
    ];

    /// <summary>
    /// タイトルだけ書かれた新しいページでボタンを押したときのプロンプト。{title} にタイトルが入る。
    /// 何も書いていない状態から始められるようにするためのもので、
    /// 「何をやりたいか」をタイトルで指示する使い方を想定している。
    /// </summary>
    [JsonPropertyName("topicStartPromptTemplate")]
    public string[] TopicStartPromptTemplate { get; set; } =
    [
        "新しいページが開かれました。ページのタイトルは「{title}」です。",
        "これからこのタイトルの内容に取り組みます。相手はまだ何も書いていません。",
        "タイトルを手がかりに、最初の 1 問を出してください。いきなり解説を始めないこと。",
        "出力はそのまま OneNote に挿入されます。プレーンテキストのみ（マークダウン記法なし）。",
        "{figureGuide}",
    ];

    /// <summary>
    /// タイトルが手書きだったときに、開始プロンプトの先頭へ足す 1 行。{image} に
    /// タイトルのインクを描いた PNG のパスが入る。OneNote はタイトル欄の手書きを
    /// テキストにしてくれない (日本語では recognizedText が空白になる) ので、
    /// 画像として読ませる。
    /// </summary>
    [JsonPropertyName("titleInkPromptLine")]
    public string TitleInkPromptLine { get; set; } =
        "まず {image} を Read ツールで読み取ってください。これはこのページのタイトルを手書きしたものです。"
        + "そこに書かれているのが、これからやりたい内容です。";

    /// <summary>会話を継続 (resume) するときの短いプロンプト。文脈はセッション側にある前提。</summary>
    [JsonPropertyName("resumePromptTemplate")]
    public string[] ResumePromptTemplate { get; set; } =
    [
        "手書きノートの続きを送ります。{image} を Read ツールで読み取ってください。",
        "これまでの会話の文脈を踏まえて、日本語で応答してください。",
        "出力はそのまま OneNote に挿入されます。プレーンテキストのみ（マークダウン記法なし）、長くても15行程度。",
        "{textSection}",
    ];

    /// <summary>
    /// 図の描き方の説明。プロンプトに {figureGuide} と書くとここが展開される。
    /// 各プロンプトに同じ説明を重複させないための共通ブロック。
    /// </summary>
    [JsonPropertyName("figureGuide")]
    public string[] FigureGuide { get; set; } =
    [
        "図やインクをノートに描けます。応答の中に次の行を書くと、その位置に挿入されます:",
        "  {{image: <PNGの絶対パス> | width=200}}   … 図の画像を挿入 (width は省略可、単位はpt)",
        "  {{ink: 0,0 100,0 100,60 | color=#1F4E79 | width=2}}   … 折れ線を1本描く (点は x,y をスペース区切り)",
        "  {{ink-overlay: 20,20 120,90 | color=#D40000}}   … 送られた画像の座標系のまま、元のノートに重ねて描く (赤ペンの添削・補助線)",
        "  {{ink-overlay: circle 300,400 r=25 | color=#D40000 | width=3}}   … 円 (中心と半径で書ける)",
        "  {{ink-overlay: wave 100,300 260,300 | color=#D40000 | width=2}}   … 波線 (2点を結ぶ。amp=4 で振幅を変えられる)",
        "  {{ink-overlay: ? 280,285 size=20 | color=#D40000 | width=2}}   … 「?」マーク (x,y は記号の上端中央)",
        "丸付けの流儀 (必ずこれに従うこと):",
        "- 正解のとき: 答えの数字と単位を circle で囲む。緑や青のチェック (✓) は使わない",
        "- 間違い・怪しいときは、答えには何も付けない。怪しい途中式のその行に wave で波線を引き、行末に ? を置く。",
        "  × や レ点、斜線は書かない (どこがどう違うかは本文の言葉で説明する)",
        "- 答えは合っているが途中式が怪しい、という場合は両方付けてよい (答えに○、その行に波線+?)",
        "- 色はどちらも #D40000 (赤ペン)。width は ○ が 3、波線と ? が 2 くらい",
        "ルール:",
        "- ink の座標は送られた画像のピクセル座標系。ink-overlay は画像上で見えている位置にそのまま重なる",
        "- 画像が送られていない（テキストだけを受け取った）ときは、ink の座標はポイント単位として扱われる。200 くらいで手のひらサイズ。ink-overlay は重ねる先が無いので使えない",
        "- ノートのページ背景は現在「{noteTheme}」。線画は画像より ink を優先すること。ink は OneNote が背景に合わせて自動で反転するので、白いノートでも黒いノートでも読める",
        "- どうしても画像が必要なとき (円・正確な角度・フォントを使うラベルなど) は、背景を必ず透明 (RGBA) にする。白で塗りつぶすと暗いノートの上で白い板のように浮く",
        "- 透明背景の画像は OneNote に反転されないので、線や文字の色はこの背景でそのまま読める色にする。真っ黒や真っ白は避け、中間の色 (青・橙・緑など) を使うと両方の背景で読める",
        "- ink の連続する行はまとめて1つの図になる。線分図・面積図・矢印はこれで描く",
        "- 正確な作図 (角度・長さ・円) が要るときは、自分で計算して PNG を作り {{image:}} で貼る",
        "- 図は説明の補助。まず言葉で1個教えて、必要なときだけ描く",
    ];

    public string FigureGuideText => string.Join("\n", FigureGuide)
        .Replace("{noteTheme}", IsDarkNote ? "暗い背景 (ダークモード)" : "白い背景");

    // ---- 音声入力 ----

    /// <summary>丸ボタンの長押しで音声入力するか。</summary>
    [JsonPropertyName("voiceInput")]
    public bool VoiceInput { get; set; } = true;

    /// <summary>長押しと判定するまでのミリ秒。</summary>
    [JsonPropertyName("longPressMs")]
    public int LongPressMs { get; set; } = 400;

    /// <summary>録音の上限秒数。</summary>
    [JsonPropertyName("maxRecordSeconds")]
    public int MaxRecordSeconds { get; set; } = 60;

    /// <summary>録音デバイス番号。null なら既定のマイク。</summary>
    [JsonPropertyName("audioDevice")]
    public int? AudioDevice { get; set; }

    /// <summary>
    /// 文字起こしエンジン: "auto" / "whisper" / "openai" / "windows"。
    /// auto は使えるものを whisper → openai → windows の順に選ぶ。
    /// </summary>
    [JsonPropertyName("sttEngine")]
    public string SttEngine { get; set; } = "auto";

    /// <summary>認識する言語 (ja / en)。</summary>
    [JsonPropertyName("sttLanguage")]
    public string SttLanguage { get; set; } = "ja";

    /// <summary>
    /// 文字起こしのヒント文 (whisper の --prompt / OpenAI の prompt)。
    /// null なら言語に合わせた既定文、空文字ならヒントなし。
    /// よく使う専門用語を含めた自然な文にしておくと、その語が拾われやすくなる。
    /// </summary>
    [JsonPropertyName("sttPrompt")]
    public string? SttPrompt { get; set; }

    /// <summary>
    /// sessionScope が "page" のとき、新しいページで会話を作り直す際に、
    /// 前のページのセッションに申し送りを書かせて引き継ぐか。
    /// 会話が際限なく伸びるのを防ぎつつ、話の流れは保つための仕組み。
    /// </summary>
    [JsonPropertyName("sessionHandoff")]
    public bool SessionHandoff { get; set; } = true;

    /// <summary>前のページのセッションに投げる、申し送りを書かせるためのプロンプト。</summary>
    [JsonPropertyName("handoffSummaryPrompt")]
    public string[] HandoffSummaryPrompt { get; set; } =
    [
        "この会話をここで区切ります。次の会話に引き継ぐための申し送りを書いてください。",
        "- 相手が何に取り組み、どこまで進んだか",
        "- つまずいた点と、その原因として見立てたこと",
        "- 次に何をするつもりだったか",
        "事実だけを 400 字以内のプレーンテキストで。前置き・感想・見出しは書かない。",
    ];

    public string HandoffSummaryPromptText => string.Join("\n", HandoffSummaryPrompt);

    /// <summary>新しいセッションの最初のプロンプトに添える申し送り。{summary} が中身に置き換わる。</summary>
    [JsonPropertyName("handoffPromptTemplate")]
    public string[] HandoffPromptTemplate { get; set; } =
    [
        "前の会話からの申し送りです (あなたはその会話自体は覚えていません):",
        "---",
        "{summary}",
        "---",
        "これを踏まえたうえで、以下に答えてください。",
        "",
    ];

    public string HandoffPromptText => string.Join("\n", HandoffPromptTemplate);

    /// <summary>whisper-cli.exe のパス。null なら whisper は使わない。</summary>
    [JsonPropertyName("whisperExe")]
    public string? WhisperExe { get; set; }

    /// <summary>whisper のモデル (ggml-*.bin) のパス。</summary>
    [JsonPropertyName("whisperModel")]
    public string? WhisperModel { get; set; }

    /// <summary>
    /// OpenAI の文字起こし API を使うためのキー。null なら環境変数 OPENAI_API_KEY を見る。
    /// whisper をローカルに置けない PC 用のフォールバック。
    /// </summary>
    [JsonPropertyName("openaiApiKey")]
    public string? OpenAiApiKey { get; set; }

    /// <summary>
    /// OpenAI の文字起こしモデル。gpt-4o-transcribe は mini より日本語の取りこぼしが少ない
    /// (数秒の発話なら料金差は無視できる)。アカウントが未対応なら "whisper-1" にする。
    /// </summary>
    [JsonPropertyName("openaiSttModel")]
    public string OpenAiSttModel { get; set; } = "gpt-4o-transcribe";

    /// <summary>設定または環境変数から OpenAI のキーを取り出す。無ければ null。</summary>
    public string? ResolveOpenAiApiKey()
    {
        if (!string.IsNullOrWhiteSpace(OpenAiApiKey)) return OpenAiApiKey.Trim();
        var env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    /// <summary>文字起こしテキストの行頭に付ける記号。</summary>
    [JsonPropertyName("voicePrefix")]
    public string VoicePrefix { get; set; } = "💬 ";

    /// <summary>文字起こしテキストの色 (CSS hex)。</summary>
    [JsonPropertyName("voiceColor")]
    public string VoiceColor { get; set; } = "#6B7280";

    /// <summary>音声入力に、前回から新しく書かれた部分も添えるか。</summary>
    [JsonPropertyName("voiceIncludesWriting")]
    public bool VoiceIncludesWriting { get; set; } = true;

    /// <summary>音声入力時のプロンプト。{voice} に文字起こし、{image} に画像パスが入る。</summary>
    [JsonPropertyName("voicePromptTemplate")]
    public string[] VoicePromptTemplate { get; set; } =
    [
        "手書きノートについて口頭で質問されました。文字起こしした発言は次のとおりです。",
        "---",
        "{voice}",
        "---",
        "{voiceWriting}",
        "この質問に日本語で答えてください。出力はそのまま OneNote に挿入されます。プレーンテキストのみ（マークダウン記法なし）。",
        "{figureGuide}",
    ];

    public string VoicePromptTemplateText => string.Join("\n", VoicePromptTemplate);

    /// <summary>
    /// ノートのページ背景。"auto" (既定) は OneNote の「表示 → 背景色の切り替え」の
    /// 状態から判定する。"dark" / "light" で固定もできる。
    /// Claude が図を作るときの色選びに使う。
    /// </summary>
    [JsonPropertyName("noteTheme")]
    public string NoteTheme { get; set; } = "auto";

    /// <summary>ページ背景が暗いか。</summary>
    public bool IsDarkNote => NoteTheme.Trim().ToLowerInvariant() switch
    {
        "dark" => true,
        "light" => false,
        _ => NoteThemeDetector.IsDarkCanvas(),
    };

    /// <summary>画面右下のフローティングボタンを表示するか。</summary>
    [JsonPropertyName("floatButton")]
    public bool FloatButton { get; set; } = true;

    /// <summary>フローティングボタンの直径 (px)。</summary>
    [JsonPropertyName("floatButtonSize")]
    public int FloatButtonSize { get; set; } = 56;

    /// <summary>
    /// resume に失敗したとき、前のセッションの記録を新しいセッションに読ませて
    /// 文脈を引き継がせるか。
    /// </summary>
    [JsonPropertyName("sessionTakeover")]
    public bool SessionTakeover { get; set; } = true;

    /// <summary>
    /// 引き継ぎ時に本来のプロンプトの前に差し込む指示。
    /// {sessionId} {sessionFile} {sessionSizeMb} {reason} が置換される。
    /// </summary>
    [JsonPropertyName("sessionTakeoverPromptTemplate")]
    public string[] SessionTakeoverPromptTemplate { get; set; } =
    [
        "【前回の続きです】",
        "セッション {sessionId} の再開に失敗しました（理由: {reason}）。",
        "そのセッションの記録が次のファイルに JSON Lines 形式で残っています（約 {sessionSizeMb} MB）。",
        "  {sessionFile}",
        "まずこれを読んで、これまでのやり取りを引き継いでください。",
        "読み方の注意:",
        "- 1 行が 1 メッセージです。base64 画像を含む行は数 MB あるので、ファイル全体を Read してはいけません",
        "- PowerShell や Bash で message.content の中の type=\"text\" の text だけを抜き出し、",
        "  末尾 30〜50 メッセージ程度に絞って読むのが確実です",
        "- 目的は文脈の把握です。相手が誰で、何を学んでいて、直前に何を話していたかが分かれば十分です",
        "引き継いだうえで、以下の依頼に答えてください。",
        "----------------",
    ];

    public string SessionTakeoverPromptText => string.Join("\n", SessionTakeoverPromptTemplate);

    /// <summary>
    /// 「更新を確認して適用」で一緒に走らせるコマンド。上から順に実行する。
    /// ClaudeNote 本体の更新の有無にかかわらず実行される。
    /// </summary>
    [JsonPropertyName("updateHooks")]
    public UpdateHook[] UpdateHooks { get; set; } = [];

    /// <summary>セクション名で切り替える設定プロファイル。上から順に評価し最初の一致を適用。</summary>
    [JsonPropertyName("profiles")]
    public ConfigProfile[] Profiles { get; set; } = [];

    public string PromptTemplateText => string.Join("\n", PromptTemplate);
    public string TextOnlyPromptTemplateText => string.Join("\n", TextOnlyPromptTemplate);
    public string ResumePromptTemplateText => string.Join("\n", ResumePromptTemplate);
    public string TopicStartPromptTemplateText => string.Join("\n", TopicStartPromptTemplate);

    /// <summary>セクション名に一致するプロファイルを重ねた実効設定を返す。一致なしなら自身を返す。</summary>
    public AppConfig ResolveForSection(string sectionName, out string matchedLabel)
    {
        var profile = Profiles.FirstOrDefault(p => WildcardMatch(p.Match, sectionName));
        if (profile == null)
        {
            matchedLabel = "(グローバル)";
            return this;
        }
        matchedLabel = profile.Match;
        return new AppConfig
        {
            Hotkey = Hotkey,
            Model = profile.Model ?? Model,
            ClaudePath = ClaudePath,
            TimeoutSeconds = TimeoutSeconds,
            ResponseColor = ResponseColor,
            ResponseWidthChars = ResponseWidthChars,
            ResponseCharWidthPt = ResponseCharWidthPt,
            CaptureBackground = CaptureBackground,
            IncludeOverlapping = IncludeOverlapping,
            OverlapMarginPt = OverlapMarginPt,
            OverlapMaxObjects = OverlapMaxObjects,
            InsertPosition = InsertPosition,
            KeepArtifacts = KeepArtifacts,
            SessionScope = profile.SessionScope ?? SessionScope,
            WorkspaceDir = profile.WorkspaceDir ?? WorkspaceDir,
            Engine = Engine,
            NodePath = NodePath,
            SidecarDir = SidecarDir,
            FigureGuide = FigureGuide,
            NoteTheme = NoteTheme,
            SessionTakeover = SessionTakeover,
            SessionTakeoverPromptTemplate = SessionTakeoverPromptTemplate,
            FloatButton = FloatButton,
            FloatButtonSize = FloatButtonSize,
            VoiceInput = VoiceInput,
            LongPressMs = LongPressMs,
            MaxRecordSeconds = MaxRecordSeconds,
            AudioDevice = AudioDevice,
            SttEngine = SttEngine,
            SttLanguage = SttLanguage,
            SttPrompt = SttPrompt,
            SessionHandoff = SessionHandoff,
            HandoffSummaryPrompt = profile.HandoffSummaryPrompt ?? HandoffSummaryPrompt,
            HandoffPromptTemplate = profile.HandoffPromptTemplate ?? HandoffPromptTemplate,
            WhisperExe = WhisperExe,
            WhisperModel = WhisperModel,
            OpenAiApiKey = OpenAiApiKey,
            OpenAiSttModel = OpenAiSttModel,
            VoicePrefix = VoicePrefix,
            VoiceColor = VoiceColor,
            VoiceIncludesWriting = VoiceIncludesWriting,
            VoicePromptTemplate = profile.VoicePromptTemplate ?? VoicePromptTemplate,
            TopicStartPromptTemplate = profile.TopicStartPromptTemplate ?? TopicStartPromptTemplate,
            TitleInkPromptLine = TitleInkPromptLine,
            AllowedTools = profile.AllowedTools ?? AllowedTools,
            AddDirs = profile.AddDirs ?? AddDirs,
            PromptTemplate = profile.PromptTemplate ?? PromptTemplate,
            ResumePromptTemplate = profile.ResumePromptTemplate ?? ResumePromptTemplate,
            TextOnlyPromptTemplate = profile.TextOnlyPromptTemplate ?? TextOnlyPromptTemplate,
            Profiles = [],
        };
    }

    private static bool WildcardMatch(string pattern, string value)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }

    /// <summary>実際に編集すべき個人設定のパス。</summary>
    public static string UserConfigPath => Path.Combine(Logger.BaseDir, "appsettings.json");

    /// <summary>exe に同梱されたテンプレート (appsettings.sample.json)。初回の雛形。</summary>
    public static string SampleConfigPath => Path.Combine(AppContext.BaseDirectory, "appsettings.sample.json");

    /// <summary>実際に読み込まれる設定ファイル。</summary>
    public static string EffectiveConfigPath =>
        File.Exists(UserConfigPath) ? UserConfigPath : SampleConfigPath;

    /// <summary>
    /// 設定を読み込む。壊れていれば例外を投げる。
    /// 再読み込み時に既定値へ黙って戻ってしまうのを防ぐため、起動時の Load とは分けている。
    /// </summary>
    public static AppConfig LoadStrict(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) ?? throw new InvalidOperationException("設定が空です。");
    }

    /// <summary>
    /// 設定を読み込む。%LOCALAPPDATA%\ClaudeNote\appsettings.json (個人設定) を使い、
    /// 無ければ同梱テンプレートをそこにコピーしてから読む。
    /// 以降ユーザーが編集する場所は常に 1 箇所だけになる。
    /// </summary>
    public static AppConfig LoadDefault()
    {
        var userPath = UserConfigPath;
        if (!File.Exists(userPath) && File.Exists(SampleConfigPath))
        {
            try
            {
                Directory.CreateDirectory(Logger.BaseDir);
                File.Copy(SampleConfigPath, userPath);
                Logger.Log($"個人設定を作成しました: {userPath}");
            }
            catch (Exception ex)
            {
                Logger.Log($"個人設定の作成に失敗、テンプレートを直接使用します: {ex.Message}");
                Logger.Log($"設定ファイル: {SampleConfigPath}");
                return Load(SampleConfigPath);
            }
        }
        var path = File.Exists(userPath) ? userPath : SampleConfigPath;
        Logger.Log($"設定ファイル: {path}");
        return Load(path);
    }

    /// <summary>
    /// 直近の <see cref="Load"/> が失敗した理由。成功していれば null。
    /// 失敗すると workspaceDir もプロファイルも API キーも既定値に戻ってしまい、
    /// ログを見るまで気づけないため、起動時に知らせるために残しておく。
    /// </summary>
    public static string? LastLoadError { get; private set; }

    public static AppConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                LastLoadError = null;
                return new AppConfig();
            }
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? new AppConfig();
            LastLoadError = null;
            return loaded;
        }
        catch (Exception ex)
        {
            Logger.Log($"設定ファイルの読み込みに失敗、デフォルトを使用: {ex.Message}");
            LastLoadError = $"{path}\n\n{ex.Message}";
            return new AppConfig();
        }
    }
}
