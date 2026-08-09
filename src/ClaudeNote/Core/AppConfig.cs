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
            KeepArtifacts = KeepArtifacts,
            SessionScope = SessionScope,
            WorkspaceDir = profile.WorkspaceDir ?? WorkspaceDir,
            Engine = Engine,
            NodePath = NodePath,
            SidecarDir = SidecarDir,
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

    /// <summary>
    /// 設定を読み込む。%LOCALAPPDATA%\ClaudeNote\appsettings.json (個人設定) があれば
    /// そちらを優先し、無ければ exe 隣の appsettings.json (テンプレート) を使う。
    /// 個人のパスやプロンプトをリポジトリに置かないための分離。
    /// </summary>
    public static AppConfig LoadDefault()
    {
        var userPath = Path.Combine(Logger.BaseDir, "appsettings.json");
        var path = File.Exists(userPath)
            ? userPath
            : Path.Combine(AppContext.BaseDirectory, "appsettings.json");
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
