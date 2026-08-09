using System.IO;
using System.Windows;

namespace ClaudeNote;

public sealed record AskResult(string Response, string? PngPath, string ArtifactsDir, bool Resumed);

/// <summary>
/// キャプチャ → 透明PNG化 → Claude 問い合わせ (会話セッション継続) → ノートへ挿入、のメインフロー。
/// セクション名に応じて設定プロファイル (作業ディレクトリ・プロンプト等) を切り替える。
/// COM 呼び出しがあるため UI (STA) スレッドから開始すること。
/// </summary>
public sealed class AskFlow
{
    private readonly AppConfig _config;

    public AskFlow(AppConfig config) => _config = config;

    private static string ResolveWorkspace(AppConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.WorkspaceDir)
            ? Path.Combine(Logger.BaseDir, "workspace")
            : Environment.ExpandEnvironmentVariables(cfg.WorkspaceDir);

    public async Task<AskResult> RunAsync(Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var onenote = new OneNoteApp();

        var (pageId, sectionId) = onenote.GetCurrentContext();
        if (string.IsNullOrEmpty(pageId))
            throw new UserFacingException("OneNote でページを開いた状態で実行してください。");

        // セクション名でプロファイルを解決
        var cfg = _config;
        if (_config.Profiles.Length > 0 && !string.IsNullOrEmpty(sectionId))
        {
            var sectionName = onenote.GetSectionName(sectionId);
            cfg = _config.ResolveForSection(sectionName, out var matched);
            Logger.Log($"セクション '{sectionName}' → プロファイル {matched}");
        }

        var pageXml = onenote.GetPageXml(pageId);
        var sel = PageXml.ParseSelection(pageXml);
        if (sel.IsEmpty)
            throw new UserFacingException("OneNote 上で何も選択されていません。なげなわ選択やドラッグで範囲を選んでから実行してください。");

        var workspace = ResolveWorkspace(cfg);
        var dir = Path.Combine(workspace, "captures", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);

        RenderResult? render = null;
        if (sel.HasVisual)
        {
            render = SelectionRenderer.RenderToPng(sel, Path.Combine(dir, "capture.png"));
            if (render != null)
                Logger.Log($"キャプチャ: {render.WidthPx}x{render.HeightPx}px ink={render.InkCount} img={render.ImageCount} skip={render.SkippedInk}");
        }

        // 会話セッションの解決: セクション (既定) またはページ単位で claude セッションを継続する
        var scopeKey = cfg.SessionScope.ToLowerInvariant() switch
        {
            "off" => null,
            "page" => pageId,
            _ => !string.IsNullOrEmpty(sectionId) ? sectionId : pageId,
        };
        var store = scopeKey != null ? new SessionStore() : null;
        var entry = scopeKey != null ? store!.Get(scopeKey) : null;
        var resumeId = string.IsNullOrWhiteSpace(entry?.SessionId) ? null : entry!.SessionId;
        var runCwd = entry?.Cwd is { Length: > 0 } cwd && Directory.Exists(cwd) ? cwd : workspace;

        var prompt = BuildPrompt(cfg, sel, render, resumed: resumeId != null);

        var addDirs = cfg.ExpandedAddDirs;
        ClaudeResult result;
        try
        {
            result = await AskEngineAsync(cfg, prompt, runCwd, resumeId, addDirs, onProgress, ct);
        }
        catch (SessionResumeException ex)
        {
            // 保存していたセッションが消えている場合は新規会話でやり直す
            Logger.Log($"resume 失敗、新規セッションで再試行: {ex.Message}");
            prompt = BuildPrompt(cfg, sel, render, resumed: false);
            result = await AskEngineAsync(cfg, prompt, runCwd, null, addDirs, onProgress, ct);
        }

        // -p --resume は毎回新しいセッション ID にフォークする実装もあるため、常に最新 ID を保存する
        if (scopeKey != null && !string.IsNullOrWhiteSpace(result.SessionId))
            store!.Update(scopeKey, result.SessionId!);

        var anchor = sel.BoundsPt ?? sel.FallbackBoundsPt ?? new Rect(72, 72, 240, 20);
        var updateXml = PageXml.BuildResponseXml(pageId, anchor, result.Text, cfg.ResponseColor);
        onenote.UpdatePage(updateXml);

        if (cfg.KeepArtifacts)
        {
            try { File.WriteAllText(Path.Combine(dir, "response.txt"), result.Text); } catch { }
        }
        else
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        return new AskResult(result.Text, render?.PngPath, dir, resumeId != null);
    }

    private static Task<ClaudeResult> AskEngineAsync(AppConfig cfg, string prompt, string cwd, string? resumeId,
        string[] addDirs, Action<string>? onProgress, CancellationToken ct) =>
        cfg.Engine.Equals("cli", StringComparison.OrdinalIgnoreCase)
            ? ClaudeCli.AskAsync(cfg, prompt, cwd, resumeId, addDirs, ct)
            : ClaudeSidecar.Instance.AskAsync(cfg, prompt, cwd, resumeId, addDirs, onProgress, ct);

    private static string BuildPrompt(AppConfig cfg, Selection sel, RenderResult? render, bool resumed)
    {
        if (render != null)
        {
            var textSection = string.IsNullOrWhiteSpace(sel.Text)
                ? ""
                : $"\n選択範囲に含まれていたテキスト:\n---\n{sel.Text}\n---";
            var template = resumed ? cfg.ResumePromptTemplateText : cfg.PromptTemplateText;
            return template
                .Replace("{image}", render.PngPath)
                .Replace("{textSection}", textSection);
        }
        if (!string.IsNullOrWhiteSpace(sel.Text))
            return cfg.TextOnlyPromptTemplateText.Replace("{text}", sel.Text);

        throw new UserFacingException("選択範囲から読み取れる内容がありませんでした。");
    }
}
