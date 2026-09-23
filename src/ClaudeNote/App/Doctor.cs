using System.IO;
using System.Text.RegularExpressions;

namespace ClaudeNote;

/// <summary>
/// セットアップが揃っているかを一通り見る。新しい PC に入れたときと、
/// 設定を手で書き換えたあとの確認用。
///
/// とくにプロファイルは %LOCALAPPDATA% にあってリポジトリの外なので、
/// コード側でプレースホルダ名を変えても追従しない。古い名前は差し替えられず
/// 生の文字列のまま送られ、エラーも出ないまま画像が届かなくなる
/// ({voiceSelection} → {voiceWriting} の改名で実際に起きた)。ここで気づけるようにする。
/// </summary>
internal static class Doctor
{
    /// <summary>コードが差し替えるプレースホルダ。ここに無い名前は綴り違いか古い名前。</summary>
    private static readonly string[] Known =
    [
        "voice", "voiceWriting", "image", "figureGuide", "textSection", "text", "title",
    ];

    /// <summary>テンプレートの種類ごとに、無いと困るプレースホルダ。</summary>
    private static readonly Dictionary<string, string[]> Required = new(StringComparer.OrdinalIgnoreCase)
    {
        ["promptTemplate"] = ["image"],
        ["resumePromptTemplate"] = ["image"],
        ["textOnlyPromptTemplate"] = ["text"],
        ["voicePromptTemplate"] = ["voice", "voiceWriting"],
        ["topicStartPromptTemplate"] = ["title"],
    };

    // {{image: ...}} のような図の指示は二重波かっこなので拾わない
    private static readonly Regex Placeholder =
        new(@"(?<!\{)\{([a-zA-Z][a-zA-Z0-9]*)\}(?!\})", RegexOptions.Compiled);

    private sealed class Report
    {
        public int Errors;
        public int Warnings;

        public void Ok(string label, string detail) => Console.WriteLine($"  OK    {label,-26}{detail}");

        public void Warn(string label, string detail)
        {
            Warnings++;
            Console.WriteLine($"  注意  {label,-26}{detail}");
        }

        public void Error(string label, string detail)
        {
            Errors++;
            Console.WriteLine($"  NG    {label,-26}{detail}");
        }
    }

    public static int Run(AppConfig config)
    {
        var r = new Report();

        Console.WriteLine("ClaudeNote セットアップ診断\n");
        Console.WriteLine("[実行環境]");
        CheckEnvironment(config, r);

        Console.WriteLine("\n[設定] " + AppConfig.UserConfigPath);
        CheckGlobal(config, r);

        Console.WriteLine("\n[プロファイル]");
        if (config.Profiles.Length == 0)
            Console.WriteLine("  (なし — セクション名による切り替えはしない)");
        foreach (var profile in config.Profiles)
            CheckProfile(profile, r);

        Console.WriteLine("\n[共通のテンプレート]");
        CheckTemplates("(グローバル)", new Dictionary<string, string[]>
        {
            ["promptTemplate"] = config.PromptTemplate,
            ["resumePromptTemplate"] = config.ResumePromptTemplate,
            ["textOnlyPromptTemplate"] = config.TextOnlyPromptTemplate,
            ["voicePromptTemplate"] = config.VoicePromptTemplate,
            ["topicStartPromptTemplate"] = config.TopicStartPromptTemplate,
        }, r);

        Console.WriteLine();
        if (r.Errors > 0)
            Console.WriteLine($"NG {r.Errors} 件 / 注意 {r.Warnings} 件。上の NG を直してください。");
        else if (r.Warnings > 0)
            Console.WriteLine($"NG はありません。注意 {r.Warnings} 件 (動きますが、意図どおりか確認を)。");
        else
            Console.WriteLine("問題は見つかりませんでした。");
        return r.Errors > 0 ? 1 : 0;
    }

    private static void CheckEnvironment(AppConfig config, Report r)
    {
        var node = AppPaths.Expand(config.NodePath) ?? "node";
        r.Ok("node", node == "node" ? "PATH から探す" : node);

        try
        {
            var script = ClaudeSidecar.ResolveScript(config.SidecarDir);
            var modules = Path.Combine(Path.GetDirectoryName(script)!, "node_modules");
            r.Ok("サイドカー", script);
            if (Directory.Exists(modules)) r.Ok("node_modules", "あり");
            else r.Error("node_modules", $"ありません。sidecar で npm install を実行してください ({modules})");
        }
        catch (Exception ex)
        {
            r.Error("サイドカー", ex.Message);
        }

        try { r.Ok("claude CLI", ClaudeCli.Resolve(config.ClaudePath)); }
        catch (Exception ex) { r.Error("claude CLI", ex.Message); }

        var repo = Updater.FindRepoRoot();
        if (repo != null) r.Ok("リポジトリ", repo);
        else r.Warn("リポジトリ", "見つかりません (トレイからの更新は使えません)");
    }

    private static void CheckGlobal(AppConfig config, Report r)
    {
        r.Ok("モデル", config.Model ?? "(未指定 = Claude Code の既定)");
        r.Ok("会話の区切り", config.SessionScope);
        r.Ok("ホットキー", config.Hotkey);

        var workspace = AppPaths.Expand(config.WorkspaceDir);
        if (workspace == null) r.Ok("作業ディレクトリ", "(未指定 = %LOCALAPPDATA%\\ClaudeNote\\workspace)");
        else if (Directory.Exists(workspace)) r.Ok("作業ディレクトリ", workspace);
        else r.Error("作業ディレクトリ", $"ありません: {workspace}");

        if (!config.VoiceInput)
        {
            r.Ok("音声入力", "無効");
            return;
        }
        var engine = config.SttEngine.ToLowerInvariant();
        var whisperExe = AppPaths.Expand(config.WhisperExe);
        var whisperModel = AppPaths.Expand(config.WhisperModel);
        var hasWhisper = whisperExe != null && File.Exists(whisperExe)
            && whisperModel != null && File.Exists(whisperModel);
        var hasOpenAi = config.ResolveOpenAiApiKey() != null;

        var available = new List<string>();
        if (hasWhisper) available.Add("whisper");
        if (hasOpenAi) available.Add("openai");
        available.Add("windows");
        r.Ok("音声入力", $"engine={engine} / 使えるもの: {string.Join(" → ", available)}");

        if (engine == "whisper" && !hasWhisper)
            r.Error("whisper", $"exe かモデルがありません (exe={whisperExe ?? "未設定"} / model={whisperModel ?? "未設定"})");
        else if (engine == "openai" && !hasOpenAi)
            r.Error("openai", "キーがありません (openaiApiKey か環境変数 OPENAI_API_KEY)");
        else if (engine == "auto" && !hasWhisper && !hasOpenAi)
            r.Warn("音声入力", "whisper も openai も無いので Windows 標準になります (精度は劣ります)");
    }

    private static void CheckProfile(ConfigProfile p, Report r)
    {
        Console.WriteLine($"  --- {p.Match} ---");
        r.Ok("モデル", p.Model ?? "(グローバルを継承)");
        r.Ok("会話の区切り", p.SessionScope ?? "(グローバルを継承)");

        var workspace = AppPaths.Expand(p.WorkspaceDir);
        if (workspace == null) r.Ok("作業ディレクトリ", "(グローバルを継承)");
        else if (Directory.Exists(workspace)) r.Ok("作業ディレクトリ", workspace);
        else r.Error("作業ディレクトリ", $"ありません: {workspace}");

        foreach (var dir in p.AddDirs ?? [])
        {
            var expanded = Environment.ExpandEnvironmentVariables(dir);
            if (!Directory.Exists(expanded)) r.Warn("addDirs", $"ありません: {expanded}");
        }

        CheckTemplates(p.Match, new Dictionary<string, string[]?>
        {
            ["promptTemplate"] = p.PromptTemplate,
            ["resumePromptTemplate"] = p.ResumePromptTemplate,
            ["textOnlyPromptTemplate"] = p.TextOnlyPromptTemplate,
            ["voicePromptTemplate"] = p.VoicePromptTemplate,
            ["topicStartPromptTemplate"] = p.TopicStartPromptTemplate,
        }.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value!), r);
    }

    private static void CheckTemplates(string owner, Dictionary<string, string[]> templates, Report r)
    {
        foreach (var (name, lines) in templates)
        {
            if (lines.Length == 0) continue;
            var text = string.Join("\n", lines);
            var used = Placeholder.Matches(text).Select(m => m.Groups[1].Value).Distinct().ToArray();

            var unknown = used.Where(u => !Known.Contains(u, StringComparer.Ordinal)).ToArray();
            var missing = Required.TryGetValue(name, out var req)
                ? req.Where(x => !used.Contains(x, StringComparer.Ordinal)).ToArray()
                : [];

            if (unknown.Length > 0)
                r.Error(name, $"知らないプレースホルダ: {string.Join(", ", unknown.Select(u => "{" + u + "}"))} "
                    + "— 綴り違いか、コード側で改名された古い名前です");
            if (missing.Length > 0)
                r.Error(name, $"必要なプレースホルダがありません: {string.Join(", ", missing.Select(m => "{" + m + "}"))} "
                    + "— これが無いと、その内容は Claude に渡りません");
            if (unknown.Length == 0 && missing.Length == 0)
                r.Ok(name, $"{lines.Length} 行 / {string.Join(", ", used.Select(u => "{" + u + "}"))}");
        }
    }
}
