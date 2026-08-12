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
    /// Claude に送るキャプチャ画像の背景。"auto" (既定) はインクの明るさから
    /// 白か暗色かを選ぶ。"white" / "black" / "transparent" / "#RRGGBB" も指定可。
    /// 透明にすると、表示側の合成色によっては黒インクが読めなくなる。
    /// </summary>
    [JsonPropertyName("captureBackground")]
    public string CaptureBackground { get; set; } = "auto";

    /// <summary>
    /// 回答の挿入位置。"belowAll" (既定) はページ全体の下端 (空白部分) に置くため
    /// 既存の内容と重ならない。"belowSelection" は選択範囲の真下に置く。
    /// x 座標はどちらも選択範囲の左端に揃える。
    /// </summary>
    [JsonPropertyName("insertPosition")]
    public string InsertPosition { get; set; } = "belowAll";

    public bool InsertBelowAll =>
        !InsertPosition.Equals("belowSelection", StringComparison.OrdinalIgnoreCase);

    /// <summary>キャプチャ PNG と応答を残すか。</summary>
    [JsonPropertyName("keepArtifacts")]
    public bool KeepArtifacts { get; set; } = true;

    /// <summary>
    /// 会話セッションの継続単位。"section" = OneNote のセクションごとに会話を継続 (既定)、
    /// "page" = ページごと、"off" = 毎回新規会話。
    /// </summary>
    [JsonPropertyName("sessionScope")]
    public string SessionScope { get; set; } = "section";

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
        "以下は OneNote 上で選択されたテキストです。日本語で簡潔に応答してください。出力はプレーンテキストのみ。",
        "---",
        "{text}",
        "---",
    ];

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
        "ルール:",
        "- ink の座標は送られた画像のピクセル座標系。ink-overlay は画像上で見えている位置にそのまま重なる",
        "- ink の連続する行はまとめて1つの図になる。線分図・面積図・矢印はこれで描く",
        "- 正確な作図 (角度・長さ・円) が要るときは、自分で計算して PNG を作り {{image:}} で貼る",
        "- 図は説明の補助。まず言葉で1個教えて、必要なときだけ描く",
    ];

    public string FigureGuideText => string.Join("\n", FigureGuide);

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

    /// <summary>文字起こしエンジン: "auto" / "whisper" / "windows"。</summary>
    [JsonPropertyName("sttEngine")]
    public string SttEngine { get; set; } = "auto";

    /// <summary>認識する言語 (ja / en)。</summary>
    [JsonPropertyName("sttLanguage")]
    public string SttLanguage { get; set; } = "ja";

    /// <summary>whisper-cli.exe のパス。null なら whisper は使わない。</summary>
    [JsonPropertyName("whisperExe")]
    public string? WhisperExe { get; set; }

    /// <summary>whisper のモデル (ggml-*.bin) のパス。</summary>
    [JsonPropertyName("whisperModel")]
    public string? WhisperModel { get; set; }

    /// <summary>文字起こしテキストの行頭に付ける記号。</summary>
    [JsonPropertyName("voicePrefix")]
    public string VoicePrefix { get; set; } = "💬 ";

    /// <summary>文字起こしテキストの色 (CSS hex)。</summary>
    [JsonPropertyName("voiceColor")]
    public string VoiceColor { get; set; } = "#6B7280";

    /// <summary>音声入力に選択範囲のキャプチャ画像も添えるか。</summary>
    [JsonPropertyName("voiceIncludesSelection")]
    public bool VoiceIncludesSelection { get; set; } = true;

    /// <summary>音声入力時のプロンプト。{voice} に文字起こし、{image} に画像パスが入る。</summary>
    [JsonPropertyName("voicePromptTemplate")]
    public string[] VoicePromptTemplate { get; set; } =
    [
        "手書きノートについて口頭で質問されました。文字起こしした発言は次のとおりです。",
        "---",
        "{voice}",
        "---",
        "{voiceSelection}",
        "この質問に日本語で答えてください。出力はそのまま OneNote に挿入されます。プレーンテキストのみ（マークダウン記法なし）。",
        "{figureGuide}",
    ];

    public string VoicePromptTemplateText => string.Join("\n", VoicePromptTemplate);

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

    /// <summary>セクション名で切り替える設定プロファイル。上から順に評価し最初の一致を適用。</summary>
    [JsonPropertyName("profiles")]
    public ConfigProfile[] Profiles { get; set; } = [];

    public string PromptTemplateText => string.Join("\n", PromptTemplate);
    public string TextOnlyPromptTemplateText => string.Join("\n", TextOnlyPromptTemplate);
    public string ResumePromptTemplateText => string.Join("\n", ResumePromptTemplate);

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
            CaptureBackground = CaptureBackground,
            InsertPosition = InsertPosition,
            KeepArtifacts = KeepArtifacts,
            SessionScope = SessionScope,
            WorkspaceDir = profile.WorkspaceDir ?? WorkspaceDir,
            Engine = Engine,
            NodePath = NodePath,
            SidecarDir = SidecarDir,
            FigureGuide = FigureGuide,
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
            WhisperExe = WhisperExe,
            WhisperModel = WhisperModel,
            VoicePrefix = VoicePrefix,
            VoiceColor = VoiceColor,
            VoiceIncludesSelection = VoiceIncludesSelection,
            VoicePromptTemplate = profile.VoicePromptTemplate ?? VoicePromptTemplate,
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

    public static AppConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppConfig();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Logger.Log($"設定ファイルの読み込みに失敗、デフォルトを使用: {ex.Message}");
            return new AppConfig();
        }
    }
}
