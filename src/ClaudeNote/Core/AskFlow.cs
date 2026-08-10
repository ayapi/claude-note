using System.IO;
using System.Windows;

namespace ClaudeNote;

public sealed record AskResult(string Response, string? PngPath, string ArtifactsDir, bool Resumed,
    string? VoiceText = null);

/// <summary>
/// キャプチャ → 透明PNG化 → Claude 問い合わせ (会話セッション継続) → ノートへ挿入、のメインフロー。
/// セクション名に応じて設定プロファイル (作業ディレクトリ・プロンプト等) を切り替える。
/// COM 呼び出しがあるため UI (STA) スレッドから開始すること。
/// </summary>
public sealed class AskFlow
{
    private readonly Func<AppConfig> _configProvider;

    /// <summary>設定は実行のたびに取得する (編集がすぐ反映されるようにするため)。</summary>
    public AskFlow(Func<AppConfig> configProvider) => _configProvider = configProvider;

    private AppConfig _config => _configProvider();

    private static string ResolveWorkspace(AppConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.WorkspaceDir)
            ? Path.Combine(Logger.BaseDir, "workspace")
            : Environment.ExpandEnvironmentVariables(cfg.WorkspaceDir);

    /// <summary>
    /// 音声入力の実行。録音済み WAV を文字起こしし、吹き出しとして先に挿入してから
    /// Claude に問い合わせ、回答をその下に入れる。
    /// </summary>
    public async Task<AskResult> RunVoiceAsync(string wavPath, Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        using var onenote = new OneNoteApp();

        var (pageId, sectionId) = onenote.GetCurrentContext();
        if (string.IsNullOrEmpty(pageId))
            throw new UserFacingException("OneNote でページを開いた状態で実行してください。");

        var cfg = ResolveConfig(onenote, sectionId);
        var workspace = ResolveWorkspace(cfg);
        var dir = Path.Combine(workspace, "captures", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-voice");
        Directory.CreateDirectory(dir);

        // 録音を成果物フォルダへ移し、あとから聞き直せるようにする
        var keptWav = Path.Combine(dir, "voice.wav");
        try { File.Move(wavPath, keptWav, overwrite: true); wavPath = keptWav; } catch { }

        onProgress?.Invoke("文字起こし中…");
        var voiceText = await Transcriber.TranscribeAsync(cfg, wavPath, ct);
        if (string.IsNullOrWhiteSpace(voiceText))
            throw new UserFacingException("音声を認識できませんでした。もう一度お試しください。");
        ct.ThrowIfCancellationRequested();

        // 選択範囲があれば一緒に送る (「この図の面積は?」のような使い方)
        var pageXml = onenote.GetPageXmlSelectionOnly(pageId);
        var sel = PageXml.ParseSelection(pageXml);
        RenderResult? render = null;
        if (cfg.VoiceIncludesSelection && sel.HasVisual)
        {
            // 描画に必要なときだけ ISF/画像込みで取り直す
            sel = PageXml.ParseSelection(onenote.GetPageXml(pageId));
            render = SelectionRenderer.RenderToPng(sel, Path.Combine(dir, "capture.png"));
            if (render != null)
                Logger.Log($"音声入力に選択範囲を添付: {render.WidthPx}x{render.HeightPx}px");
        }

        // 1 段階目: 文字起こしを吹き出しとして先に入れる
        var anchor = PageXml.ComputeInsertAnchor(pageXml, sel, cfg.InsertBelowAll);
        var bubble = cfg.VoicePrefix + voiceText;
        onenote.UpdatePage(PageXml.BuildResponseXml(pageId, anchor, [new TextPart(bubble)], cfg.VoiceColor, null));
        onProgress?.Invoke("文字起こしを挿入しました。回答を待っています…");

        // 2 段階目: Claude に問い合わせて回答を吹き出しの下に入れる
        // (挿入位置は InsertParts が実測するので、ここで先に決めておく必要はない)
        var (scopeKey, store, entry) = ResolveSession(cfg, pageId, sectionId);
        var resumeId = string.IsNullOrWhiteSpace(entry?.SessionId) ? null : entry!.SessionId;
        var runCwd = entry?.Cwd is { Length: > 0 } cwd && Directory.Exists(cwd) ? cwd : workspace;

        var voiceSelection = render != null
            ? $"あわせて、ノート上で選択されていた範囲の画像を送ります。まず {render.PngPath} を Read ツールで読み取ってから答えてください。"
            : "";
        var prompt = cfg.VoicePromptTemplateText
            .Replace("{voice}", voiceText)
            .Replace("{voiceSelection}", voiceSelection)
            .Replace("{image}", render?.PngPath ?? "")
            .Replace("{figureGuide}", cfg.FigureGuideText);

        var addDirs = cfg.ExpandedAddDirs;
        ClaudeResult result;
        try
        {
            result = await AskEngineAsync(cfg, prompt, runCwd, resumeId, addDirs, onProgress, ct);
        }
        catch (SessionResumeException ex)
        {
            Logger.Log($"resume 失敗、新規セッションで再試行: {ex.Message}");
            result = await AskEngineAsync(cfg, prompt, runCwd, null, addDirs, onProgress, ct);
        }

        if (scopeKey != null && !string.IsNullOrWhiteSpace(result.SessionId))
            store!.Update(scopeKey, result.SessionId!);

        var parts = ResponseParser.Parse(result.Text);
        InsertParts(onenote, pageId, sel, cfg, parts, render?.Map);

        if (cfg.KeepArtifacts)
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, "voice.txt"), voiceText);
                File.WriteAllText(Path.Combine(dir, "response.txt"), result.Text);
            }
            catch { }
        }

        return new AskResult(result.Text, render?.PngPath, dir, resumeId != null, voiceText);
    }

    private AppConfig ResolveConfig(OneNoteApp onenote, string sectionId)
    {
        if (_config.Profiles.Length == 0 || string.IsNullOrEmpty(sectionId)) return _config;
        var sectionName = onenote.GetSectionName(sectionId);
        var cfg = _config.ResolveForSection(sectionName, out var matched);
        Logger.Log($"セクション '{sectionName}' → プロファイル {matched}");
        return cfg;
    }

    private static (string? ScopeKey, SessionStore? Store, SessionEntry? Entry) ResolveSession(
        AppConfig cfg, string pageId, string sectionId)
    {
        var scopeKey = cfg.SessionScope.ToLowerInvariant() switch
        {
            "off" => null,
            "page" => pageId,
            _ => !string.IsNullOrEmpty(sectionId) ? sectionId : pageId,
        };
        var store = scopeKey != null ? new SessionStore() : null;
        return (scopeKey, store, scopeKey != null ? store!.Get(scopeKey) : null);
    }

    public async Task<AskResult> RunAsync(Action<string>? onProgress = null, CancellationToken ct = default)
    {
        using var onenote = new OneNoteApp();

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

        // まずバイナリ抜きの軽い XML で「何が選ばれているか」だけ調べる。
        // インクの多いページでは ISF 込みの取得に数十秒かかり、その間 OneNote を
        // 掴み続けることになるため、必要なときだけ取りに行く
        var sel = PageXml.ParseSelection(onenote.GetPageXmlSelectionOnly(pageId));
        if (sel.IsEmpty)
            throw new UserFacingException("OneNote 上で何も選択されていません。なげなわ選択やドラッグで範囲を選んでから実行してください。");
        if (sel.HasVisual)
            sel = PageXml.ParseSelection(onenote.GetPageXml(pageId));  // 描画に ISF/画像が要る

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

        var parts = ResponseParser.Parse(result.Text);
        var figures = parts.Count(p => p is ImagePart or InkPart);
        if (figures > 0) Logger.Log($"応答に図が {figures} 個含まれています");
        InsertParts(onenote, pageId, sel, cfg, parts, render?.Map);

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

    /// <summary>
    /// 応答をノートへ挿入する。
    /// テキストの高さは折り返しによって変わり、こちらでは正確に見積もれないため、
    /// 本文は 1 つずつ入れて、そのつど実際の下端を測り直してから次を置く。
    /// (見積もりで一括挿入すると 2 つ目以降が少し上にずれて重なる)
    /// </summary>
    private static void InsertParts(OneNoteApp onenote, string pageId, Selection sel, AppConfig cfg,
        IReadOnlyList<ResponsePart> parts, CaptureMap? map)
    {
        // 重ね書き (補助線) は元の図の上に置くもので、本文の流れとは無関係
        var overlays = parts.OfType<InkPart>().Where(p => p.Overlay).ToList();
        if (overlays.Count > 0 && map != null)
        {
            onenote.UpdatePage(PageXml.BuildResponseXml(pageId, new Rect(), [.. overlays], cfg.ResponseColor, map));
            Logger.Log($"補助線を {overlays.Count} 個重ねました");
        }

        var flow = parts.Where(p => p is not InkPart { Overlay: true }).ToList();
        if (flow.Count == 0) return;

        // ページ下端に積む場合は、挿入のたびに実測できる
        if (cfg.InsertBelowAll)
        {
            foreach (var part in flow)
            {
                var anchor = PageXml.ComputeInsertAnchor(onenote.GetPageXmlBasic(pageId), sel, belowAll: true);
                Logger.Log($"挿入位置: x={anchor.X:0.#} y={anchor.Bottom:0.#} ({part.GetType().Name})");
                onenote.UpdatePage(PageXml.BuildResponseXml(pageId, anchor, [part], cfg.ResponseColor, map));
            }
            return;
        }

        // 選択範囲の真下に置く場合は実測できないので、従来どおり見積もりで一括挿入する
        var fallback = PageXml.ComputeInsertAnchor(onenote.GetPageXmlBasic(pageId), sel, belowAll: false);
        Logger.Log($"挿入位置: x={fallback.X:0.#} y={fallback.Bottom:0.#} (belowSelection、一括)");
        onenote.UpdatePage(PageXml.BuildResponseXml(pageId, fallback, [.. flow], cfg.ResponseColor, map));
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
                .Replace("{figureGuide}", cfg.FigureGuideText)
                .Replace("{textSection}", textSection);
        }
        if (!string.IsNullOrWhiteSpace(sel.Text))
            return cfg.TextOnlyPromptTemplateText
                .Replace("{figureGuide}", cfg.FigureGuideText)
                .Replace("{text}", sel.Text);

        throw new UserFacingException("選択範囲から読み取れる内容がありませんでした。");
    }
}
