using System.IO;
using System.Windows;

namespace ClaudeNote;

public sealed record AskResult(string Response, string? PngPath, string ArtifactsDir, string SessionMode,
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
            render = SelectionRenderer.RenderToPng(sel, Path.Combine(dir, "capture.png"), cfg.CaptureBackground);
            if (render != null)
                Logger.Log($"音声入力に選択範囲を添付: {render.WidthPx}x{render.HeightPx}px");
        }
        else
        {
            Logger.Log($"音声入力: 添付する選択範囲なし (ink={sel.Ink.Count} img={sel.Images.Count} " +
                $"textLen={sel.Text.Length} 添付設定={cfg.VoiceIncludesSelection} / OneNoteの報告: {sel.Diagnostics})");
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
        // 音声入力のプロンプトは継続用と初回用を分けていないので、そのまま使う
        var outcome = await AskWithContinuityAsync(cfg, _ => prompt, runCwd, resumeId, addDirs, onProgress, ct);
        var result = outcome.Result;

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

        return new AskResult(result.Text, render?.PngPath, dir, outcome.SessionMode, voiceText);
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
        {
            Logger.Log($"選択なし (OneNoteの報告: {sel.Diagnostics})");
            throw new UserFacingException("OneNote 上で何も選択されていません。なげなわ選択やドラッグで範囲を選んでから実行してください。");
        }
        Logger.Log($"選択: ink={sel.Ink.Count} img={sel.Images.Count} textLen={sel.Text.Length} " +
            $"(OneNoteの報告: {sel.Diagnostics})");
        if (sel.HasVisual)
            sel = PageXml.ParseSelection(onenote.GetPageXml(pageId));  // 描画に ISF/画像が要る

        var workspace = ResolveWorkspace(cfg);
        var dir = Path.Combine(workspace, "captures", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);

        RenderResult? render = null;
        if (sel.HasVisual)
        {
            render = SelectionRenderer.RenderToPng(sel, Path.Combine(dir, "capture.png"), cfg.CaptureBackground);
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

        var addDirs = cfg.ExpandedAddDirs;
        var outcome = await AskWithContinuityAsync(cfg,
            resumed => BuildPrompt(cfg, sel, render, resumed),
            runCwd, resumeId, addDirs, onProgress, ct);
        var result = outcome.Result;

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

        return new AskResult(result.Text, render?.PngPath, dir, outcome.SessionMode);
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

    private sealed record AskOutcome(ClaudeResult Result, string SessionMode);

    /// <summary>
    /// 会話の継続を試み、失敗したら前セッションの記録を読ませて引き継がせる。
    /// 黙って新規会話に落とすと、家庭教師がそれまでの学習内容を失ったまま答えてしまう。
    /// </summary>
    /// <param name="buildPrompt">
    /// 引数は「会話の続きとして扱うか」。文脈のない新規会話に落ちるときは、
    /// 「続きだよ」と書かれた継続用プロンプトではなく初回用を使う必要がある。
    /// </param>
    private static async Task<AskOutcome> AskWithContinuityAsync(AppConfig cfg, Func<bool, string> buildPrompt,
        string cwd, string? resumeId, string[] addDirs, Action<string>? onProgress, CancellationToken ct)
    {
        if (resumeId == null)
            return new AskOutcome(
                await AskEngineAsync(cfg, buildPrompt(false), cwd, null, addDirs, onProgress, ct), "新規会話");

        try
        {
            return new AskOutcome(
                await AskEngineAsync(cfg, buildPrompt(true), cwd, resumeId, addDirs, onProgress, ct), "会話の続き");
        }
        catch (SessionResumeException ex)
        {
            Logger.Log($"resume 失敗: {ex.Message}");

            var file = cfg.SessionTakeover ? SessionArchive.Find(resumeId) : null;
            if (file == null)
            {
                Logger.Log(cfg.SessionTakeover
                    ? $"セッション記録が見つからないため、文脈なしの新規会話で続けます ({resumeId})"
                    : "引き継ぎが無効なため、新規会話で続けます");
                // 文脈が無いので「続きだよ」ではなく初回用のプロンプトで聞く
                return new AskOutcome(
                    await AskEngineAsync(cfg, buildPrompt(false), cwd, null, addDirs, onProgress, ct),
                    "新規会話 (文脈なし)");
            }

            // 記録ファイルを読めるようにディレクトリを許可に加える
            var dir = Path.GetDirectoryName(file)!;
            var withArchive = addDirs.Contains(dir) ? addDirs : [.. addDirs, dir];

            var takeover = cfg.SessionTakeoverPromptText
                .Replace("{sessionId}", resumeId)
                .Replace("{sessionFile}", file)
                .Replace("{sessionSizeMb}", SessionArchive.SizeMb(file).ToString("0.0"))
                .Replace("{reason}", Summarize(ex.Message));

            Logger.Log($"前セッションの記録を読ませて引き継ぎます: {file}");
            onProgress?.Invoke("前回の記録を読み込んで引き継いでいます…");
            // 記録から文脈を復元するので、継続用のプロンプトで聞いてよい
            return new AskOutcome(
                await AskEngineAsync(cfg, takeover + "\n" + buildPrompt(true), cwd, null, withArchive, onProgress, ct),
                "前セッションを引き継ぎ");
        }
    }

    private static string Summarize(string message)
    {
        var text = message.Replace("セッション継続に失敗: ", "").Replace('\n', ' ').Trim();
        return text.Length <= 160 ? text : text[..160] + "…";
    }

    private static string BuildPrompt(AppConfig cfg, Selection sel, RenderResult? render, bool resumed)
    {
        if (render != null)
        {
            var textSection = string.IsNullOrWhiteSpace(sel.Text)
                ? ""
                : $"\n選択範囲に含まれていたテキスト:\n---\n{sel.Text}\n---";
            var template = resumed ? cfg.ResumePromptTemplateText : cfg.PromptTemplateText;
            Logger.Log($"使用プロンプト: {(resumed ? "resumePromptTemplate" : "promptTemplate")} ({template.Length}文字)");
            return template
                .Replace("{image}", render.PngPath)
                .Replace("{figureGuide}", cfg.FigureGuideText)
                .Replace("{textSection}", textSection);
        }
        if (!string.IsNullOrWhiteSpace(sel.Text))
        {
            Logger.Log("使用プロンプト: textOnlyPromptTemplate");
            return cfg.TextOnlyPromptTemplateText
                .Replace("{figureGuide}", cfg.FigureGuideText)
                .Replace("{text}", sel.Text);
        }

        throw new UserFacingException("選択範囲から読み取れる内容がありませんでした。");
    }
}
