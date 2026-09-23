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

        // 前回から書き足されたぶんを一緒に送る (「これで合ってる?」のような使い方)
        var capture = cfg.VoiceIncludesWriting
            ? CaptureNewWriting(onenote, pageId, cfg, onProgress)
            : null;
        var sel = capture?.Selection ?? new Selection { PageId = pageId };
        RenderResult? render = null;
        if (capture is { IsEmpty: false } && sel.HasRenderableData)
        {
            render = SelectionRenderer.RenderToPng(sel, Path.Combine(dir, "capture.png"), cfg.CaptureBackground);
            if (render != null)
                Logger.Log($"音声入力に書いた内容を添付: {render.WidthPx}x{render.HeightPx}px");
        }
        else
        {
            Logger.Log($"音声入力: 添付する書き込みなし (添付設定={cfg.VoiceIncludesWriting}, " +
                $"差分={capture?.ChangeCount ?? 0} 個)");
        }

        // 1 段階目: 文字起こしを吹き出しとして先に入れる
        var anchor = PageXml.ComputeInsertAnchor(onenote.GetPageXmlBasic(pageId), sel, cfg.InsertBelowAll);
        var bubble = cfg.VoicePrefix + voiceText;
        onenote.UpdatePage(PageXml.BuildResponseXml(pageId, anchor, [new TextPart(bubble)], cfg.VoiceColor, null, cfg.ResponseWidthPt));
        onProgress?.Invoke("文字起こしを挿入しました。回答を待っています…");

        // 2 段階目: Claude に問い合わせて回答を吹き出しの下に入れる
        // (挿入位置は InsertParts が実測するので、ここで先に決めておく必要はない)
        var (scopeKey, lineageKey, store, entry) = ResolveSession(cfg, pageId, sectionId);
        var resumeId = string.IsNullOrWhiteSpace(entry?.SessionId) ? null : entry!.SessionId;
        var runCwd = entry?.Cwd is { Length: > 0 } cwd && Directory.Exists(cwd) ? cwd : workspace;

        // 書かれたものを必ず添える。テキストが落ちていて
        // 「本文が空で届いていない」と言われる不具合があった
        var writingParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(sel.Text))
            writingParts.Add($"前回から新しく書かれたテキスト:\n---\n{sel.Text}\n---");
        if (render != null)
            writingParts.Add($"あわせて、前回から新しく書かれた部分の画像を送ります。まず {render.PngPath} を Read ツールで読み取ってから答えてください。");
        var voiceWriting = writingParts.Count > 0
            ? string.Join("\n", writingParts)
            : "（前回から新しく書かれたものはありません。発言だけで答えてください）";
        Logger.Log($"音声入力に添える内容: テキスト {sel.Text.Length}文字 / 画像 {(render != null ? "あり" : "なし")}");
        var prompt = CheckPlaceholders(cfg.VoicePromptTemplateText
            .Replace("{voice}", voiceText)
            .Replace("{voiceWriting}", voiceWriting)
            .Replace("{image}", render?.PngPath ?? "")
            .Replace("{figureGuide}", cfg.FigureGuideText), "voicePromptTemplate");

        var addDirs = cfg.ExpandedAddDirs;
        var handoff = await PrepareHandoffAsync(cfg, store, lineageKey, resumeId == null, runCwd, addDirs, ct);
        // 音声入力のプロンプトは継続用と初回用を分けていないので、そのまま使う
        var outcome = await AskWithContinuityAsync(cfg, _ => Prepend(handoff, prompt),
            runCwd, resumeId, addDirs, onProgress, ct);
        var result = outcome.Result;

        SaveSession(store, scopeKey, lineageKey, result.SessionId);

        var parts = ResponseParser.Parse(result.Text);
        InsertParts(onenote, pageId, sel, cfg, parts, render?.Map);

        // 応答を入れ終えてから基準を取り直す (自分が書いたものを次の差分に含めない)
        CommitBaseline(onenote, pageId);

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

    /// <summary>前回の送信から新しく書かれたぶん。</summary>
    private sealed record Capture(Selection Selection, PageSnapshot Snapshot, int ChangeCount)
    {
        public bool IsEmpty => ChangeCount == 0;
    }

    /// <summary>
    /// 前回この ページ へ送ったときからの差分を取り込む。
    ///
    /// 手で範囲選択させる代わりに「前回から書き足されたもの」を送る。OneNote の
    /// ページ XML は手書きのストロークごと・段落ごとに objectID を持っているので、
    /// 前回の指紋と突き合わせれば増えたぶんだけが分かる。
    /// 基準はページ単位で保存され、応答を挿入したあとに取り直す
    /// (Claude 自身が書いた応答や添削を次の差分に含めないため)。
    /// </summary>
    private static Capture CaptureNewWriting(OneNoteApp onenote, string pageId, AppConfig cfg,
        Action<string>? onProgress)
    {
        // まずバイナリ抜きの軽い XML で「何が増えたか」だけ調べる。
        // インクの多いページでは ISF 込みの取得に数十秒かかるため、必要なときだけ取りに行く
        var snapshot = PageSnapshot.FromXml(onenote.GetPageXmlBasic(pageId));
        var baseline = BaselineStore.Load(pageId);
        var changes = snapshot.ChangesSince(baseline);

        if (baseline == null)
            Logger.Log($"このページの基準がまだ無いので、いまある {changes.Count} 個すべてを送ります");

        if (changes.Count == 0)
        {
            Logger.Log($"差分なし (ページ上のオブジェクト {snapshot.Objects.Count} 個)");
            return new Capture(new Selection { PageId = pageId }, snapshot, 0);
        }

        var inkCount = changes.Count(c => !c.Object.IsText);
        var textCount = changes.Count - inkCount;
        Logger.Log($"差分: 手書き・画像 {inkCount} 個 / 段落 {textCount} 個 " +
            $"(ページ全体 {snapshot.Objects.Count} 個, 基準 {baseline?.Fingerprints.Count ?? 0} 個)");

        // 描画に使う ISF はバイナリ込みの XML にしか無いので、図があるときだけ取りに行く
        Selection sel;
        if (inkCount > 0)
        {
            var ids = changes.Select(c => c.Object.ObjectId).ToHashSet();

            // 書いた場所に既にあった図も一緒に送る (補助線だけでは意味が通らないため)
            if (cfg.IncludeOverlapping)
            {
                var (expanded, _, addedCount) = PageSnapshot.ExpandToOverlapping(
                    snapshot, ids, PageSnapshot.BoundsOf(changes),
                    cfg.OverlapMarginPt, cfg.OverlapMaxObjects);
                if (addedCount > 0)
                    Logger.Log($"書いた場所に重なる既存の図を {addedCount} 個も一緒に送ります");
                ids = expanded;
            }

            onProgress?.Invoke($"書いた内容を取得しています… (手書き {ids.Count} 個)");
            sel = PageSnapshot.BuildSelection(onenote.GetPageXml(pageId), ids);
        }
        else
        {
            sel = new Selection { PageId = pageId, Text = PageSnapshot.TextOf(changes) };
        }
        sel.BoundsPt ??= PageSnapshot.BoundsOf(changes);
        return new Capture(sel, snapshot, changes.Count);
    }

    /// <summary>
    /// 送信後の姿を基準として残す。応答や添削を入れ終わったあとに呼ぶこと。
    /// ここで取り直さないと、Claude が書いたものが次の差分に混ざる。
    /// </summary>
    private static void CommitBaseline(OneNoteApp onenote, string pageId)
    {
        try
        {
            var after = PageSnapshot.FromXml(onenote.GetPageXmlBasic(pageId));
            BaselineStore.Save(after.ToBaseline());
            Logger.Log($"基準を更新しました: {after.Objects.Count} 個");
        }
        catch (Exception ex)
        {
            Logger.Log($"基準の更新に失敗しました (次回の差分が広くなります): {ex.Message}");
        }
    }

    private AppConfig ResolveConfig(OneNoteApp onenote, string sectionId)
    {
        if (_config.Profiles.Length == 0 || string.IsNullOrEmpty(sectionId)) return _config;
        var sectionName = onenote.GetSectionName(sectionId);
        var cfg = _config.ResolveForSection(sectionName, out var matched);
        Logger.Log($"セクション '{sectionName}' → プロファイル {matched}");
        return cfg;
    }

    /// <summary>
    /// この実行で使う会話セッションを決める。
    /// LineageKey は、ページ単位で会話を切っているときに「同じセクションの直前のセッション」を
    /// 辿るための鍵。ページが変わっても話の流れを引き継ぐために使う。
    /// </summary>
    private static (string? ScopeKey, string? LineageKey, SessionStore? Store, SessionEntry? Entry) ResolveSession(
        AppConfig cfg, string pageId, string sectionId)
    {
        var scope = cfg.SessionScope.ToLowerInvariant();
        var scopeKey = scope switch
        {
            "off" => null,
            "page" => pageId,
            _ => !string.IsNullOrEmpty(sectionId) ? sectionId : pageId,
        };
        // セクション単位のときは会話がそもそも続くので、引き継ぎは要らない
        var lineageKey = scope == "page" && !string.IsNullOrEmpty(sectionId) ? sectionId + "|lineage" : null;
        var store = scopeKey != null ? new SessionStore() : null;
        return (scopeKey, lineageKey, store, scopeKey != null ? store!.Get(scopeKey) : null);
    }

    /// <summary>
    /// 新しいページで会話を作り直すとき、前のページのセッションに申し送りを書かせて持ってくる。
    /// 会話を短く保ったまま流れを切らさないための仕組みで、ページ 1 枚につき 1 回だけ走る。
    /// 失敗しても本題は止めない (前回の申し送りが残っていればそれを使う)。
    /// </summary>
    internal static async Task<string?> PrepareHandoffAsync(AppConfig cfg, SessionStore? store, string? lineageKey,
        bool startingFresh, string cwd, string[] addDirs, CancellationToken ct)
    {
        if (!cfg.SessionHandoff || store == null || lineageKey == null || !startingFresh) return null;

        var lineage = store.Get(lineageKey);
        if (lineage == null || string.IsNullOrWhiteSpace(lineage.SessionId))
        {
            // セクション単位からページ単位へ切り替えた直後は系統の記録がまだ無い。
            // それまで使っていたセクションのセッションを 1 度だけ引き継ぎ元にして、
            // 積み上げた文脈を申し送りの形で残す
            var section = store.Get(lineageKey[..^"|lineage".Length]);
            if (section == null || string.IsNullOrWhiteSpace(section.SessionId)) return null;
            Logger.Log("ページ単位に切り替わったので、これまでのセクションの会話から引き継ぎます");
            lineage = section;
        }

        var previous = string.IsNullOrWhiteSpace(lineage.Summary) ? null : lineage.Summary;
        try
        {
            Logger.Log($"新しいページなので、前のセッション {lineage.SessionId} に申し送りを書かせます");
            var r = await AskEngineAsync(cfg, cfg.HandoffSummaryPromptText, cwd, lineage.SessionId,
                addDirs, null, ct);
            var summary = r.Text.Trim();
            if (summary.Length == 0)
            {
                Logger.Log("申し送りが空でした");
            }
            else
            {
                store.UpdateSummary(lineageKey, summary);
                Logger.Log($"申し送り ({summary.Length}文字): {Shorten(summary, 120)}");
                previous = summary;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Log($"申し送りの作成に失敗しました。{(previous == null ? "引き継がずに続けます" : "前回の申し送りを使います")}: {ex.Message}");
        }

        return previous == null ? null : cfg.HandoffPromptText.Replace("{summary}", previous);
    }

    private static string Shorten(string s, int max) =>
        s.Length <= max ? s.Replace("\n", " ") : s[..max].Replace("\n", " ") + "…";

    private static string Prepend(string? handoff, string prompt) =>
        string.IsNullOrEmpty(handoff) ? prompt : handoff + "\n" + prompt;

    /// <summary>
    /// 使ったセッション ID を保存する。ページ単位のときは、次に別のページを開いたときに
    /// 「直前のセッション」として辿れるよう、系統の鍵にも同じ ID を書いておく。
    /// </summary>
    private static void SaveSession(SessionStore? store, string? scopeKey, string? lineageKey, string? sessionId)
    {
        if (store == null || string.IsNullOrWhiteSpace(sessionId)) return;
        if (scopeKey != null) store.Update(scopeKey, sessionId!);
        if (lineageKey != null) store.Update(lineageKey, sessionId!);
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

        var capture = CaptureNewWriting(onenote, pageId, cfg, onProgress);

        // タイトルだけ書かれた新しいページなら、そのタイトルを「やりたいこと」として始める。
        // 何も書いていない状態から始められるようにするため (以前は音声で言うしかなかった)。
        // タイトルが手書きの回は文字が取れないので、インクを描いて画像で読ませる
        var startFromTitle = capture.IsEmpty && capture.Snapshot.IsBodyEmpty;
        var topic = startFromTitle ? capture.Snapshot.Title : null;
        Selection? titleInk = null;
        if (startFromTitle && string.IsNullOrWhiteSpace(topic))
        {
            titleInk = PageSnapshot.BuildTitleSelection(onenote.GetPageXmlBasic(pageId));
            if (titleInk != null)
                Logger.Log($"タイトルが手書きです (インク {titleInk.VisualCount} 個)。画像にして送ります");
        }
        if (capture.IsEmpty && string.IsNullOrWhiteSpace(topic) && titleInk == null)
        {
            throw new UserFacingException(capture.Snapshot.IsBodyEmpty
                ? "ページが空です。タイトルにやりたいことを書いてから実行すると、そこから始められます。"
                : "前回送ってから、まだ何も書かれていません。ノートに書いてから実行してください。");
        }
        if (topic != null) Logger.Log($"タイトルから開始: 「{topic}」");
        var sel = titleInk ?? capture.Selection;

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
        var (scopeKey, lineageKey, store, entry) = ResolveSession(cfg, pageId, sectionId);
        var resumeId = string.IsNullOrWhiteSpace(entry?.SessionId) ? null : entry!.SessionId;
        var runCwd = entry?.Cwd is { Length: > 0 } cwd && Directory.Exists(cwd) ? cwd : workspace;

        var addDirs = cfg.ExpandedAddDirs;
        var handoff = await PrepareHandoffAsync(cfg, store, lineageKey, resumeId == null, runCwd, addDirs, ct);
        var outcome = await AskWithContinuityAsync(cfg,
            resumed => Prepend(handoff,
                BuildPrompt(cfg, sel, render, resumed, topic, titleInk != null)),
            runCwd, resumeId, addDirs, onProgress, ct);
        var result = outcome.Result;

        // -p --resume は毎回新しいセッション ID にフォークする実装もあるため、常に最新 ID を保存する
        SaveSession(store, scopeKey, lineageKey, result.SessionId);

        // タイトルのインクはタイトル欄の座標系で、ページ本文の座標系ではない。
        // これを挿入位置の基準にすると左上にめり込むので (x=-0.5 y=33.9 になった)、
        // 位置は持たせず本文の既定位置へ置く。重ね書きの座標も同じ理由で合わないため渡さない
        var anchorSel = titleInk != null ? new Selection { PageId = pageId } : sel;
        var anchorMap = titleInk != null ? null : render?.Map;

        var parts = ResponseParser.Parse(result.Text);
        var figures = parts.Count(p => p is ImagePart or InkPart);
        if (figures > 0) Logger.Log($"応答に図が {figures} 個含まれています");
        InsertParts(onenote, pageId, anchorSel, cfg, parts, anchorMap);

        // 応答を入れ終えてから基準を取り直す (自分が書いたものを次の差分に含めない)
        CommitBaseline(onenote, pageId);

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
        if (overlays.Count > 0)
        {
            if (map == null)
                Logger.Log($"重ね書き {overlays.Count} 個を無視しました (キャプチャ画像が無いため位置を決められない)");
            else
            {
                onenote.UpdatePage(PageXml.BuildResponseXml(pageId, new Rect(), [.. overlays], cfg.ResponseColor, map));
                Logger.Log($"補助線を {overlays.Count} 個重ねました");
            }
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
                onenote.UpdatePage(PageXml.BuildResponseXml(pageId, anchor, [part], cfg.ResponseColor, map, cfg.ResponseWidthPt));
            }
            return;
        }

        // 書いた部分の真下に置く場合は実測できないので、従来どおり見積もりで一括挿入する
        var fallback = PageXml.ComputeInsertAnchor(onenote.GetPageXmlBasic(pageId), sel, belowAll: false);
        Logger.Log($"挿入位置: x={fallback.X:0.#} y={fallback.Bottom:0.#} (belowSelection、一括)");
        onenote.UpdatePage(PageXml.BuildResponseXml(pageId, fallback, [.. flow], cfg.ResponseColor, map, cfg.ResponseWidthPt));
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

    private static readonly System.Text.RegularExpressions.Regex UnresolvedPlaceholder =
        new(@"(?<!\{)\{[a-zA-Z][a-zA-Z0-9]*\}(?!\})",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 差し替えられなかったプレースホルダが残っていないかを見る。
    ///
    /// 設定ファイルはリポジトリの外 (%LOCALAPPDATA%) にあるため、コード側で
    /// プレースホルダ名を変えても設定は追従しない。古い名前が生の文字列のまま
    /// 送られても何のエラーも出ず、画像が届いていないことに気づけない
    /// ({voiceSelection} → {voiceWriting} の改名で実際に起きた)。
    /// </summary>
    private static string CheckPlaceholders(string prompt, string label)
    {
        var left = UnresolvedPlaceholder.Matches(prompt)
            .Select(m => m.Value).Distinct().ToArray();
        if (left.Length > 0)
            Logger.Log($"警告: {label} に差し替えられていないプレースホルダが残っています: "
                + $"{string.Join(", ", left)} (設定ファイルが古い可能性があります)");
        return prompt;
    }

    private static string BuildPrompt(AppConfig cfg, Selection sel, RenderResult? render, bool resumed,
        string? topic = null, bool titleIsInk = false)
    {
        // タイトルから始める場合。タイプしたタイトルは文字で渡せるが、
        // 手書きのタイトルは文字にできないので、描いた画像を読ませる
        if (titleIsInk || !string.IsNullOrWhiteSpace(topic))
        {
            var body = cfg.TopicStartPromptTemplateText
                .Replace("{title}", titleIsInk ? "手書き（上の画像に書かれているとおり）" : topic!)
                .Replace("{figureGuide}", cfg.FigureGuideText);
            if (!titleIsInk)
            {
                Logger.Log($"使用プロンプト: topicStartPromptTemplate (タイトル「{topic}」)");
                return CheckPlaceholders(body, "topicStartPromptTemplate");
            }
            if (render == null)
                throw new UserFacingException("手書きのタイトルを画像にできませんでした。");
            Logger.Log("使用プロンプト: topicStartPromptTemplate (タイトルは手書き)");
            return CheckPlaceholders(
                cfg.TitleInkPromptLine.Replace("{image}", render.PngPath) + "\n" + body,
                "topicStartPromptTemplate");
        }
        if (render != null)
        {
            var textSection = string.IsNullOrWhiteSpace(sel.Text)
                ? ""
                : $"\n新しく書かれたテキスト:\n---\n{sel.Text}\n---";
            var template = resumed ? cfg.ResumePromptTemplateText : cfg.PromptTemplateText;
            Logger.Log($"使用プロンプト: {(resumed ? "resumePromptTemplate" : "promptTemplate")} ({template.Length}文字)");
            return CheckPlaceholders(template
                .Replace("{image}", render.PngPath)
                .Replace("{figureGuide}", cfg.FigureGuideText)
                .Replace("{textSection}", textSection),
                resumed ? "resumePromptTemplate" : "promptTemplate");
        }
        if (!string.IsNullOrWhiteSpace(sel.Text))
        {
            Logger.Log("使用プロンプト: textOnlyPromptTemplate");
            return CheckPlaceholders(cfg.TextOnlyPromptTemplateText
                .Replace("{figureGuide}", cfg.FigureGuideText)
                .Replace("{text}", sel.Text), "textOnlyPromptTemplate");
        }

        throw new UserFacingException("書かれた内容から読み取れるものがありませんでした。");
    }
}
