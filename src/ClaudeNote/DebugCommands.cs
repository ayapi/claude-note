using System.IO;
using System.Text;

namespace ClaudeNote;

/// <summary>
/// 動作検証用のコマンドラインモード。
///   --render-test &lt;pageXml&gt; &lt;outPng&gt; : 保存済みページ XML の全 ink/画像を PNG 化
///   --capture-test                       : 前回から書かれたぶんをキャプチャして PNG 化 (挿入なし)
///   --ask-test &lt;png&gt; [sessionId]     : PNG を claude CLI に送って応答を表示 (挿入なし)。sessionId 指定で resume 検証
///   --insert-test                        : テストページを作成して挿入 → 検証 → ページ削除
///   --figure-test                        : 図 (画像 + インク) の挿入を検証 → ページ削除
///   --diff-test                          : ページの差分検出を試す。Enter のたびに「前回の送信から書かれた範囲」を出す
///   --update-hooks                       : 設定の updateHooks だけを実行する (本体の更新はしない)
///   --mic-list                           : 録音デバイスの一覧
///   --record-test [秒]                   : 指定秒だけ録音して文字起こしまで通す
///   --stt-test &lt;wav&gt;                 : 既存の WAV を文字起こしする
/// </summary>
internal static class DebugCommands
{
    /// <summary>自前のコンソールウィンドウで動かすコマンド (対話するもの)。</summary>
    private static readonly string[] Interactive = ["--diff-test", "--record-test"];

    public static int Run(string[] args, AppConfig config)
    {
        // WinExe はコンソールを持たないので、まず出力先を用意する。
        // これが無いと PowerShell から叩いても何も表示されない
        ConsoleHost.Ensure(ownWindow: Interactive.Contains(args[0]));
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try
        {
            return Dispatch(args, config);
        }
        finally
        {
            ConsoleHost.WaitBeforeClose();
        }
    }

    private static int Dispatch(string[] args, AppConfig config)
    {
        try
        {
            switch (args[0])
            {
                case "--render-test":
                    return RenderTest(args[1], args[2]);
                case "--topic-test":
                    return TopicTest(config);
                case "--title-scan":
                    return args.Length > 1 && args[1] == "section" ? TitleScanSection() : TitleScan();
                case "--paths":
                    return PathsCheck(config);
                case "--handoff-test":
                    return HandoffTest(config).GetAwaiter().GetResult();
                case "--update-check":
                    return UpdateCheck();
                case "--update-hooks":
                    return UpdateHooks(config);
                case "--diff-test":
                    return DiffTest(args.Length > 1 && int.TryParse(args[1], out var ds) ? ds : 0);
                case "--update-apply":
                    return UpdateApply();
                case "--capture-test":
                    return CaptureTest();
                case "--ask-test":
                    return AskTest(config, args[1], args.Length > 2 ? args[2] : null);
                case "--insert-test":
                    return InsertTest(config);
                case "--figure-test":
                    return FigureTest(config);
                case "--mic-list":
                    foreach (var d in AudioRecorder.ListDevices()) Console.WriteLine(d);
                    return 0;
                case "--record-test":
                    return RecordTest(config, args.Length > 1 && int.TryParse(args[1], out var s) ? s : 5);
                case "--stt-test":
                    return SttTest(config, args[1], args.Length > 2 ? args[2] : null);
                case "--voice-insert-test":
                    return VoiceInsertTest(config);
                case "--cancel-test":
                    return CancelTest(config);
                case "--multipart-test":
                    return MultipartTest(config);
                case "--width-test":
                    return WidthTest(config);
                case "--theme-test":
                    return ThemeTest(config);
                case "--takeover-test":
                    return TakeoverTest(config, args.Length > 1 ? args[1] : null);
                case "--button-preview":
                    return ButtonPreview(config);
                case "--ink-nocapture-test":
                    return InkWithoutCaptureTest(config);
                case "--bench-capture":
                    return BenchCapture(config);
                case "--voice-prompt-test":
                    return VoicePromptTest(config);
                default:
                    Console.WriteLine($"不明な引数: {args[0]}");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex}");
            return 1;
        }
    }

    /// <summary>更新の確認だけを行う (取り込みもビルドもしない)。配布先での切り分け用。</summary>
    private static int UpdateCheck()
    {
        Console.WriteLine($"リポジトリの根: {Updater.FindRepoRoot() ?? "(見つからない)"}");
        var s = Updater.Check();
        if (s.Blocker != null)
        {
            Console.WriteLine($"更新できません: {s.Blocker}");
            return 1;
        }
        Console.WriteLine(s.Behind == 0 ? "すでに最新です。" : $"{s.Behind} 件の更新があります:");
        foreach (var c in s.Commits) Console.WriteLine("  " + c);
        return 0;
    }

    /// <summary>
    /// updateHooks だけを実行して結果を出す。フックの設定を確かめるためのもので、
    /// ClaudeNote 本体の取り込み・ビルドは行わない。
    /// </summary>
    private static int UpdateHooks(AppConfig config)
    {
        if (config.UpdateHooks.Length == 0)
        {
            Console.WriteLine("updateHooks は設定されていません。");
            return 0;
        }

        var results = Updater.RunHooks(config);
        if (results.Count == 0)
        {
            Console.WriteLine("実行できるフックがありませんでした (enabled: false か command が空)。");
            return 0;
        }

        foreach (var r in results)
        {
            Console.WriteLine($"{(r.Ok ? "OK" : "NG")} {r.Label}");
            foreach (var line in r.Message.Split('\n'))
                Console.WriteLine("    " + line);
        }
        return results.All(r => r.Ok) ? 0 : 1;
    }

    /// <summary>
    /// ページの差分検出を試す。Enter を「送るボタンを押した」とみなして、
    /// 前回の送信からいま までに書かれた範囲を出す。
    ///
    /// 手で範囲選択させる代わりに「書いたものだけ送る」ことができるかを確かめるためのもの。
    /// 実際の送信や挿入は一切しない。自前のコンソールウィンドウで動く。
    /// 本番と同じく、1 回の押下につき 1 つの範囲が出る (書いている途中は何もしない)。
    /// </summary>
    private static int DiffTest(int unusedMs)
    {
        using var onenote = new OneNoteApp();
        var (pageId, _) = onenote.GetCurrentContext();
        if (string.IsNullOrEmpty(pageId))
        {
            Console.WriteLine("OneNote でページを開いた状態で実行してください。");
            return 1;
        }

        var baseline = PageSnapshot.FromXml(onenote.GetPageXmlBasic(pageId));
        Logger.Log($"--diff-test 開始: page={pageId} 基準 {baseline.Objects.Count} 個");
        Console.WriteLine($"ページ: {pageId}");
        Console.WriteLine($"基準: オブジェクト {baseline.Objects.Count} 個");
        Console.WriteLine();
        Console.WriteLine("OneNote に問題を解いて、書き終わったらこのウィンドウで Enter。");
        Console.WriteLine("(Enter = 送るボタンを押したつもり。q + Enter で終了)");
        Console.WriteLine(new string('-', 70));

        var round = 0;
        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line == null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) break;

            PageSnapshot now;
            try
            {
                now = PageSnapshot.FromXml(onenote.GetPageXmlBasic(pageId));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    取得できませんでした: {Summarize(ex.Message)}");
                Logger.Log($"--diff-test: 取得に失敗: {ex.Message}");
                continue;
            }

            round++;
            var changes = now.ChangesSince(baseline.ToBaseline());
            var added = changes.Count(c => c.Kind == PageChangeKind.Added);
            var grown = changes.Count(c => c.Kind == PageChangeKind.Grown);
            var bounds = PageSnapshot.BoundsOf(changes);

            Console.WriteLine($"[{round}] オブジェクト {baseline.Objects.Count} → {now.Objects.Count} 個 / " +
                $"新規 {added}・拡大 {grown}");
            Logger.Log($"--diff-test [{round}]: {baseline.Objects.Count} → {now.Objects.Count} 個 / " +
                $"新規 {added}・拡大 {grown} / 範囲 " + (bounds is System.Windows.Rect lb
                    ? $"x {lb.X:0}〜{lb.Right:0} y {lb.Y:0}〜{lb.Bottom:0}pt" : "なし"));

            if (bounds is not System.Windows.Rect b)
            {
                Console.WriteLine("    差分なし (何も書かれていない)。音声だけ送る場合はここに当たる");
                continue;
            }

            Console.WriteLine($"    → 送る範囲: x {b.X:0}〜{b.Right:0}pt / y {b.Y:0}〜{b.Bottom:0}pt " +
                $"({b.Width:0}x{b.Height:0}pt)");

            // 内訳は多いと読みにくいので、上から数本だけ
            foreach (var c in changes.OrderBy(c => c.Object.Rect?.Y ?? 0).ThenBy(c => c.Object.Rect?.X ?? 0).Take(5))
            {
                var r = c.Object.Rect ?? default;
                var mark = c.Kind switch
                {
                    PageChangeKind.Added => "新規",
                    PageChangeKind.Grown => "拡大",
                    _ => "書換",
                };
                Console.WriteLine($"       {mark} {c.Object.Kind,-11} x={r.X,6:0} y={r.Y,6:0} " +
                    $"{r.Width,5:0}x{r.Height,-5:0}");
            }
            if (changes.Count > 5) Console.WriteLine($"       … ほか {changes.Count - 5} 個");

            // 数字だけでは広すぎ/狭すぎが判断できないので、実際に送られる画像を出す
            var png = RenderDiff(onenote, pageId, changes, round);
            if (png != null)
            {
                Console.WriteLine($"    → 画像: {png}");
                OpenFile(png);
            }

            // 本番では応答を挿入したあとに基準を取り直す。ここでは送ったことにして更新する
            baseline = now;
        }

        Console.WriteLine(new string('-', 70));
        Console.WriteLine($"終了しました (送信相当 {round} 回)。");
        Logger.Log($"--diff-test 終了: {round} 回");
        return 0;
    }

    /// <summary>
    /// 差分に当たるインクだけを描画して、実際に Claude へ送られる画像を作る。
    /// 失敗しても差分の確認自体は続けたいので、例外は握って null を返す。
    /// </summary>
    private static string? RenderDiff(OneNoteApp onenote, string pageId,
        IReadOnlyList<PageChange> changes, int round)
    {
        try
        {
            var ids = changes.Select(c => c.Object.ObjectId).ToHashSet();
            // 描画には ISF が要るのでバイナリ込みで取り直す
            var sel = PageSnapshot.BuildSelection(onenote.GetPageXml(pageId), ids);
            if (!sel.HasRenderableData)
            {
                Console.WriteLine($"    (描画できる中身がありません: 図 {sel.VisualCount} 個)");
                return null;
            }

            var dir = Path.Combine(Logger.BaseDir, "difftest");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{round}.png");

            var result = SelectionRenderer.RenderToPng(sel, path);
            if (result == null) return null;
            Console.WriteLine($"    → 画像サイズ: {result.WidthPx}x{result.HeightPx}px");
            return path;
        }
        catch (Exception ex)
        {
            Logger.Log($"--diff-test: 差分の描画に失敗: {ex.Message}");
            Console.WriteLine($"    (描画に失敗: {Summarize(ex.Message)})");
            return null;
        }
    }

    /// <summary>既定のビューアで開く。開けなくても致命的ではない。</summary>
    private static void OpenFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"--diff-test: 画像を開けませんでした: {ex.Message}");
        }
    }

    private static string Summarize(string message)
    {
        var line = message.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return line.Length <= 80 ? line : line[..80] + "…";
    }

    /// <summary>
    /// いま開いているセクションの全ページについて、タイトルが何でできているかを一覧する。
    /// 手書きのタイトルが実在するかを探すためのもの。
    /// </summary>
    private static int TitleScanSection()
    {
        using var onenote = new OneNoteApp();
        var (_, sectionId) = onenote.GetCurrentContext();
        if (string.IsNullOrEmpty(sectionId))
        {
            Console.WriteLine("OneNote でページを開いた状態で実行してください。");
            return 1;
        }

        System.Xml.Linq.XNamespace one = PageXml.One;
        var hierarchy = System.Xml.Linq.XDocument.Parse(onenote.GetHierarchyXml());
        var pages = hierarchy.Descendants(one + "Page")
            .Where(p => (string?)p.Parent?.Attribute("ID") == sectionId)
            .ToList();

        Console.WriteLine($"セクション {onenote.GetSectionName(sectionId)}: ページ {pages.Count} 個\n");
        Console.WriteLine($"{"ページ名",-28}{"Title の中身",-22}{"本文",-8}");
        Console.WriteLine(new string('-', 62));

        foreach (var p in pages)
        {
            var id = (string?)p.Attribute("ID");
            var name = (string?)p.Attribute("name") ?? "(名前なし)";
            if (id == null) continue;
            string inside, body;
            try
            {
                var xml = onenote.GetPageXmlBasic(id);
                var doc = System.Xml.Linq.XDocument.Parse(xml);
                var title = doc.Root!.Descendants(one + "Title").FirstOrDefault();
                var kinds = title?.Descendants()
                    .Select(e => e.Name.LocalName)
                    .Where(n => n is "T" or "InkWord" or "InkDrawing" or "Image")
                    .GroupBy(n => n)
                    .Select(g => $"{g.Key}×{g.Count()}")
                    .ToList();
                inside = title == null ? "(Title 要素なし)"
                    : kinds is { Count: > 0 } ? string.Join(" ", kinds) : "(空)";
                body = PageSnapshot.FromXml(xml).Objects.Count + " 個";
            }
            catch (Exception ex)
            {
                inside = "取得できず";
                body = Summarize(ex.Message);
            }
            var shown = name.Length <= 26 ? name : name[..26] + "…";
            Console.WriteLine($"{shown,-28}{inside,-22}{body,-8}");
        }
        Console.WriteLine();
        Console.WriteLine("InkWord / InkDrawing が Title の中に出ていれば、手書きタイトルは Title に入る。");
        Console.WriteLine("どのページも T だけなら、手書きは本文側のインクになっている。");
        return 0;
    }

    /// <summary>
    /// いま開いているページのタイトル部分が XML でどう表現されているかを出す。
    /// 手書きしたタイトルが one:Title の中に入るのか、本文側のインクになるのかを
    /// 実機で確かめるためのもの。
    /// </summary>
    private static int TitleScan()
    {
        using var onenote = new OneNoteApp();
        var (pageId, _) = onenote.GetCurrentContext();
        if (string.IsNullOrEmpty(pageId))
        {
            Console.WriteLine("OneNote でページを開いた状態で実行してください。");
            return 1;
        }

        var xml = onenote.GetPageXmlBasic(pageId);
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        var page = doc.Root!;
        System.Xml.Linq.XNamespace one = PageXml.One;

        var title = page.Descendants(one + "Title").FirstOrDefault();
        Console.WriteLine($"ページ: {pageId}");
        Console.WriteLine();
        if (title == null)
        {
            Console.WriteLine("one:Title 要素がありません (タイトル未設定のページ)。");
        }
        else
        {
            Console.WriteLine("--- one:Title の中身 (生の XML) ---");
            Console.WriteLine(title.ToString());
            Console.WriteLine();
            var kinds = title.Descendants()
                .Select(e => e.Name.LocalName)
                .Where(n => n is "T" or "InkWord" or "InkDrawing" or "Image")
                .GroupBy(n => n)
                .Select(g => $"{g.Key}×{g.Count()}");
            Console.WriteLine($"タイトル内の要素: {(kinds.Any() ? string.Join(" / ", kinds) : "なし")}");
        }

        if (title != null)
        {
            Console.WriteLine();
            Console.WriteLine("--- タイトル内インクの実寸と、指定されている矩形 ---");
            Console.WriteLine($"{"#",-3}{"x",8}{"inkOrgX",9}{"w(pt)",8}{"h(pt)",8}   " +
                $"{"ISFの実寸 (DIP)",-34}{"倍率",8}");
            var n = 0;
            foreach (var el in title.Descendants()
                         .Where(e => e.Name.LocalName is "InkWord" or "InkDrawing"))
            {
                n++;
                var data = el.Element(one + "Data")?.Value;
                string natural = "(ISF なし)", ratio = "-";
                if (!string.IsNullOrWhiteSpace(data))
                {
                    try
                    {
                        var strokes = new System.Windows.Ink.StrokeCollection(
                            new MemoryStream(Convert.FromBase64String(data.Trim())));
                        var b = strokes.GetBounds();
                        natural = $"x={b.X:0.#} y={b.Y:0.#} {b.Width:0.#}x{b.Height:0.#} ({strokes.Count}本)";
                        if (double.TryParse((string?)el.Attribute("width"), out var w) && b.Width > 0.05)
                            ratio = $"{w / b.Width:0.###}";
                    }
                    catch (Exception ex) { natural = "読めず: " + Summarize(ex.Message); }
                }
                Console.WriteLine($"{n,-3}{(string?)el.Attribute("x"),8}" +
                    $"{(string?)el.Attribute("inkOriginX") ?? "-",9}" +
                    $"{(string?)el.Attribute("width"),8}{(string?)el.Attribute("height"),8}   " +
                    $"{natural,-34}{ratio,8}");
            }
            Console.WriteLine();
            Console.WriteLine("倍率が 0.75 前後で揃っていれば素直に描ける。ばらつくなら ISF の座標系が共有されている。");
        }

        Console.WriteLine();
        Console.WriteLine("--- タイトルの外 (本文側) にある上端 5 個 ---");
        foreach (var el in page.Descendants()
                     .Where(e => e.Name.LocalName is "InkDrawing" or "InkWord" or "Image" or "OE")
                     .Where(e => !e.Ancestors(one + "Title").Any())
                     .Take(5))
        {
            var pos = el.Element(one + "Position");
            Console.WriteLine($"  {el.Name.LocalName,-11} y={(string?)pos?.Attribute("y") ?? "-"} " +
                $"objectID={((string?)el.Attribute("objectID"))?[..Math.Min(12, ((string?)el.Attribute("objectID"))!.Length)] ?? "なし"}");
        }

        var snapshot = PageSnapshot.FromXml(xml);
        Console.WriteLine();
        Console.WriteLine($"ClaudeNote の解釈: タイトル=「{snapshot.Title}」 / 本文 {snapshot.Objects.Count} 個 " +
            $"({(snapshot.IsBodyEmpty ? "空" : "あり")})");
        return 0;
    }

    /// <summary>
    /// いま開いているページで、ボタンを押したらどう動くかを判定だけして見せる。
    /// タイトルから始める経路に入るかを、送信せずに確かめるためのもの。
    /// </summary>
    private static int TopicTest(AppConfig config)
    {
        using var onenote = new OneNoteApp();
        var (pageId, sectionId) = onenote.GetCurrentContext();
        if (string.IsNullOrEmpty(pageId))
        {
            Console.WriteLine("OneNote でページを開いた状態で実行してください。");
            return 1;
        }

        var cfg = config;
        if (config.Profiles.Length > 0 && !string.IsNullOrEmpty(sectionId))
        {
            var sectionName = onenote.GetSectionName(sectionId);
            cfg = config.ResolveForSection(sectionName, out var matched);
            Console.WriteLine($"セクション : {sectionName}  → プロファイル {matched}");
        }

        var snapshot = PageSnapshot.FromXml(onenote.GetPageXmlBasic(pageId));
        var baseline = BaselineStore.Load(pageId);
        var changes = snapshot.ChangesSince(baseline);

        Console.WriteLine($"タイトル   : {(string.IsNullOrWhiteSpace(snapshot.Title) ? "(なし)" : snapshot.Title)}");
        Console.WriteLine($"本文       : オブジェクト {snapshot.Objects.Count} 個 " +
            $"({(snapshot.IsBodyEmpty ? "空" : "あり")})");
        Console.WriteLine($"基準       : {(baseline == null ? "まだ無い (このページで初めて送る)" : $"{baseline.Fingerprints.Count} 個")}");
        Console.WriteLine($"差分       : {changes.Count} 個");
        Console.WriteLine();

        if (changes.Count > 0)
        {
            Console.WriteLine("→ 書かれたぶんを送ります (いつもの経路)。");
            return 0;
        }
        if (snapshot.IsBodyEmpty && string.IsNullOrWhiteSpace(snapshot.Title))
        {
            var ink = PageSnapshot.BuildTitleSelection(onenote.GetPageXmlBasic(pageId));
            if (ink != null)
            {
                Console.WriteLine($"→ タイトルが手書きです (インク {ink.VisualCount} 個、"
                    + $"ISF {ink.Ink.Count} 個)。画像にして送ります。");
                var dir = Path.Combine(Logger.BaseDir, "titletest");
                Directory.CreateDirectory(dir);
                var png = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}.png");
                var r = SelectionRenderer.RenderToPng(ink, png, cfg.CaptureBackground);
                if (r == null)
                {
                    Console.WriteLine("   ただし描画できませんでした。");
                    return 1;
                }
                Console.WriteLine($"   {r.WidthPx}x{r.HeightPx}px → {r.PngPath}");
                OpenFile(r.PngPath);
                Console.WriteLine();
                Console.WriteLine("送られるプロンプトの先頭:");
                Console.WriteLine("  " + cfg.TitleInkPromptLine.Replace("{image}", r.PngPath));
                return 0;
            }
        }
        if (snapshot.IsBodyEmpty && !string.IsNullOrWhiteSpace(snapshot.Title))
        {
            Console.WriteLine($"→ タイトル「{snapshot.Title}」から始めます。送られるプロンプト:");
            Console.WriteLine(new string('-', 70));
            Console.WriteLine(cfg.TopicStartPromptTemplateText
                .Replace("{title}", snapshot.Title)
                .Replace("{figureGuide}", cfg.FigureGuideText));
            Console.WriteLine(new string('-', 70));
            return 0;
        }
        Console.WriteLine(snapshot.IsBodyEmpty
            ? "→ 何も起きません (ページが空で、タイトルも無い)。"
            : "→ 何も起きません (前回から書き足されていない)。");
        return 1;
    }

    /// <summary>
    /// 設定と自動検出でどのパスに解決されるかを出す。新しい PC でのセットアップ確認用。
    /// </summary>
    private static int PathsCheck(AppConfig config)
    {
        Console.WriteLine($"exe の場所      : {AppContext.BaseDirectory}");
        Console.WriteLine($"設定ファイル    : {AppConfig.UserConfigPath}");
        Console.WriteLine($"作業ディレクトリ: {AppPaths.Expand(config.WorkspaceDir) ?? "(未設定)"}");
        Console.WriteLine();
        Console.WriteLine("上に辿って探す範囲:");
        foreach (var dir in AppPaths.AncestorsFromExe())
            Console.WriteLine($"  {dir}");
        Console.WriteLine();

        var ok = true;
        try
        {
            var script = ClaudeSidecar.ResolveScript(config.SidecarDir);
            Console.WriteLine($"サイドカー      : {script}");
            var modules = Path.Combine(Path.GetDirectoryName(script)!, "node_modules");
            Console.WriteLine($"  node_modules  : {(Directory.Exists(modules) ? "あり" : "なし (sidecar で npm install が必要)")}");
            if (!Directory.Exists(modules)) ok = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"サイドカー      : 解決できません — {ex.Message}");
            ok = false;
        }

        Console.WriteLine($"リポジトリ      : {Updater.FindRepoRoot() ?? "(見つからない = 更新機能は使えません)"}");
        Console.WriteLine(ok ? "\n必要なものは揃っています。" : "\n不足があります。上を確認してください。");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// ページをまたいだ申し送りの引き継ぎを、OneNote 抜きで通しで確かめる。
    /// 1) 前のページのつもりで会話を作り、事実を覚えさせる
    /// 2) 新しいページのつもりで申し送りを書かせる
    /// 3) その申し送りだけを持った新しい会話が、事実を答えられるかを見る
    /// </summary>
    private static async Task<int> HandoffTest(AppConfig config)
    {
        const string LINEAGE = "HANDOFF-TEST|lineage";
        var cwd = Path.GetTempPath();
        var cfg = config;
        var store = new SessionStore();
        var ok = true;
        void Check(string label, bool cond, string extra = "")
        {
            Console.WriteLine($"  {(cond ? "PASS" : "FAIL")}  {label}{(extra.Length > 0 ? "  " + extra : "")}");
            if (!cond) ok = false;
        }

        Console.WriteLine("1) 前のページの会話をつくる");
        var seed = await ClaudeSidecar.Instance.AskAsync(cfg,
            "これは学習セッションです。今日は「点の移動」をやっていて、相手は三角形の面積までは解けたが、"
            + "グラフの折れ曲がる点でつまずいている。次はグラフのかど探しをやる予定。"
            + "把握した、とだけ答えて。", cwd, null, [], null, CancellationToken.None);
        Check("会話ができた", !string.IsNullOrWhiteSpace(seed.SessionId), $"session={seed.SessionId?[..8]}");
        if (seed.SessionId == null) return 1;
        store.Update(LINEAGE, seed.SessionId);

        Console.WriteLine("2) 新しいページとして申し送りを作らせる");
        var handoff = await AskFlow.PrepareHandoffAsync(cfg, store, LINEAGE, startingFresh: true,
            cwd, [], CancellationToken.None);
        Check("申し送りが返る", !string.IsNullOrWhiteSpace(handoff));
        var saved = store.Get(LINEAGE);
        Check("申し送りが保存される", !string.IsNullOrWhiteSpace(saved?.Summary));
        Check("つまずきが引き継がれている", handoff?.Contains("グラフ") == true);
        Console.WriteLine("  --- 申し送り本文 ---");
        Console.WriteLine("  " + (saved?.Summary ?? "(なし)").Replace("\n", "\n  "));

        Console.WriteLine("3) 申し送りだけを持った新しい会話に聞く");
        var asked = await ClaudeSidecar.Instance.AskAsync(cfg,
            handoff + "\n相手は今日どこでつまずいていましたか。一文で答えて。",
            cwd, null, [], null, CancellationToken.None);
        Console.WriteLine("  -> " + asked.Text.Trim().Replace("\n", " "));
        Check("新しい会話が前の内容を答えられる", asked.Text.Contains("グラフ"));
        Check("前の会話とは別のセッションになっている", asked.SessionId != seed.SessionId);

        Console.WriteLine("4) 同じページの 2 回目では申し送りを作らない");
        var again = await AskFlow.PrepareHandoffAsync(cfg, store, LINEAGE, startingFresh: false,
            cwd, [], CancellationToken.None);
        Check("会話継続中は申し送りを作らない", again == null);

        Console.WriteLine(ok ? "\n結果: すべて PASS" : "\n結果: FAIL あり");
        return ok ? 0 : 1;
    }

    /// <summary>更新を実際に取り込む。トレイメニューに触れない環境での切り分け用。</summary>
    private static int UpdateApply()
    {
        var s = Updater.Check();
        if (s.Blocker != null)
        {
            Console.WriteLine($"更新できません: {s.Blocker}");
            return 1;
        }
        if (s.Behind == 0)
        {
            Console.WriteLine("すでに最新です。");
            return 0;
        }
        Console.WriteLine($"{s.Behind} 件の更新を取り込みます。ビルド後に ClaudeNote が起動します。");
        Updater.ApplyAndRestart(s.RepoRoot!);
        return 0;
    }

    private static int RenderTest(string xmlPath, string outPng)
    {
        var sel = PageXml.ParseAll(File.ReadAllText(xmlPath));
        Console.WriteLine($"ink={sel.Ink.Count} images={sel.Images.Count} textLen={sel.Text.Length}");
        var result = SelectionRenderer.RenderToPng(sel, outPng);
        if (result == null)
        {
            Console.WriteLine("描画対象なし");
            return 1;
        }
        Console.WriteLine($"OK: {result.PngPath} {result.WidthPx}x{result.HeightPx}px ink={result.InkCount} skip={result.SkippedInk} img={result.ImageCount}");
        return 0;
    }

    private static int CaptureTest()
    {
        using var onenote = new OneNoteApp();
        var pageId = onenote.GetCurrentPageId();
        if (string.IsNullOrEmpty(pageId))
        {
            Console.WriteLine("OneNote でページが開かれていません");
            return 1;
        }

        // 実際の送信と同じく「前回から書き足されたぶん」を取り込む
        var snapshot = PageSnapshot.FromXml(onenote.GetPageXmlBasic(pageId));
        var baseline = BaselineStore.Load(pageId);
        var changes = snapshot.ChangesSince(baseline);
        Console.WriteLine($"ページ全体 {snapshot.Objects.Count} 個 / 基準 {baseline?.Fingerprints.Count ?? 0} 個 " +
            $"→ 差分 {changes.Count} 個");
        if (baseline == null)
            Console.WriteLine("(このページの基準がまだ無いので、全件が差分として出ています)");
        if (changes.Count == 0)
        {
            Console.WriteLine("→ 前回から書き足されたものはありません");
            return 0;
        }

        var sel = PageSnapshot.BuildSelection(onenote.GetPageXml(pageId),
            changes.Select(c => c.Object.ObjectId).ToHashSet());
        Console.WriteLine($"差分の中身: ink={sel.Ink.Count} images={sel.Images.Count} " +
            $"textLen={sel.Text.Length} bounds={sel.BoundsPt}");
        if (!sel.HasRenderableData)
        {
            Console.WriteLine("描画できる手書き・画像はありません (テキストだけの差分)");
            return 0;
        }
        var outPng = Path.Combine(Logger.CapturesDir, "capture-test.png");
        var result = SelectionRenderer.RenderToPng(sel, outPng);
        Console.WriteLine(result == null ? "描画対象なし" : $"OK: {result.PngPath} {result.WidthPx}x{result.HeightPx}px");
        return 0;
    }

    private static int AskTest(AppConfig config, string pngPath, string? resumeSessionId = null)
    {
        var full = Path.GetFullPath(pngPath);
        var prompt = (resumeSessionId != null ? config.ResumePromptTemplateText : config.PromptTemplateText)
            .Replace("{image}", full)
            .Replace("{textSection}", "");
        var cwd = Path.GetDirectoryName(full)!;
        var addDirs = config.ExpandedAddDirs;
        Console.WriteLine($"engine: {config.Engine}");
        var result = (config.Engine.Equals("cli", StringComparison.OrdinalIgnoreCase)
                ? ClaudeCli.AskAsync(config, prompt, cwd, resumeSessionId, addDirs)
                : ClaudeSidecar.Instance.AskAsync(config, prompt, cwd, resumeSessionId, addDirs))
            .GetAwaiter().GetResult();
        Console.WriteLine($"session_id: {result.SessionId}");
        Console.WriteLine("---- Claude 応答 ----");
        Console.WriteLine(result.Text);
        return 0;
    }

    /// <summary>
    /// テキスト・画像・テキストが混ざった応答を挿入し、要素どうしが重ならないことを検証する。
    /// 折り返す長文を入れて、高さの見積もりでは足りない状況を作る。
    /// </summary>
    private static int MultipartTest(AppConfig config)
    {
        using var onenote = new OneNoteApp();
        var (sectionId, sectionName) = FindRecentSection(onenote);
        Console.WriteLine($"対象セクション: {sectionName}");

        var pngPath = Path.Combine(Path.GetTempPath(), "claudenote-multipart-test.png");
        MakeTrianglePng(pngPath);

        var pageId = onenote.CreateNewPage(sectionId);
        Console.WriteLine($"テストページ作成: {pageId}");
        try
        {
            var longLine = string.Concat(Enumerable.Repeat("これは折り返しを起こすための長い行です。", 6));
            var response = string.Join("\n",
            [
                "1つ目のテキスト。" + longLine,
                longLine,
                "{{image: " + pngPath + " | width=180}}",
                "2つ目のテキスト。" + longLine,
                "{{image: " + pngPath + " | width=120}}",
                "3つ目のテキスト。おわり。",
            ]);

            var parts = ResponseParser.Parse(response);
            Console.WriteLine($"パート数: {parts.Count}");

            var sel = new Selection { BoundsPt = new System.Windows.Rect(72, 100, 300, 20) };
            foreach (var part in parts)
            {
                var anchor = PageXml.ComputeInsertAnchor(onenote.GetPageXmlBasic(pageId), sel, belowAll: true);
                onenote.UpdatePage(PageXml.BuildResponseXml(pageId, anchor, [part], config.ResponseColor, null));
                System.Threading.Thread.Sleep(400);
            }

            // 読み戻して、ページ直下の要素が縦に重なっていないか調べる
            var final = System.Xml.Linq.XDocument.Parse(onenote.GetPageXmlBasic(pageId));
            var one = PageXml.One;
            var rects = final.Root!.Elements()
                .Select(el => new
                {
                    Name = el.Name.LocalName,
                    Pos = el.Element(one + "Position"),
                    Size = el.Element(one + "Size"),
                })
                .Where(x => x.Pos != null && x.Size != null)
                .Select(x => new
                {
                    x.Name,
                    Y = double.Parse((string)x.Pos!.Attribute("y")!, System.Globalization.CultureInfo.InvariantCulture),
                    H = double.Parse((string)x.Size!.Attribute("height")!, System.Globalization.CultureInfo.InvariantCulture),
                })
                .OrderBy(x => x.Y)
                .ToList();

            var overlaps = 0;
            for (var i = 1; i < rects.Count; i++)
            {
                var prevBottom = rects[i - 1].Y + rects[i - 1].H;
                var gap = rects[i].Y - prevBottom;
                Console.WriteLine($"  {rects[i - 1].Name,-12} 下端={prevBottom,8:0.#} → {rects[i].Name,-12} 上端={rects[i].Y,8:0.#} 隙間={gap,7:0.#}");
                if (gap < -0.5) overlaps++;
            }
            Console.WriteLine(overlaps == 0
                ? $"OK: {rects.Count} 個の要素が重なりなく縦に並びました"
                : $"NG: {overlaps} 箇所で重なっています");
            return overlaps == 0 ? 0 : 1;
        }
        finally
        {
            onenote.DeleteHierarchyItem(pageId);
            Console.WriteLine("テストページを削除しました (ノートブックのごみ箱に移動)");
            try { File.Delete(pngPath); } catch { }
        }
    }

    /// <summary>
    /// 前セッションの記録を読ませて文脈を引き継げるかを検証する。
    /// 記録ファイルを直接読ませて、そこにしか無い内容を答えられるか確かめる。
    /// </summary>
    private static int TakeoverTest(AppConfig config, string? sessionId)
    {
        sessionId ??= "2dd180c8-453a-4b95-a91b-f8a74e47c8d8";
        var file = SessionArchive.Find(sessionId);
        if (file == null)
        {
            Console.WriteLine($"セッション記録が見つかりません: {sessionId}");
            return 1;
        }
        Console.WriteLine($"記録ファイル: {file} ({SessionArchive.SizeMb(file):0.0} MB)");

        var takeover = config.SessionTakeoverPromptText
            .Replace("{sessionId}", sessionId)
            .Replace("{sessionFile}", file)
            .Replace("{sessionSizeMb}", SessionArchive.SizeMb(file).ToString("0.0"))
            .Replace("{reason}", "テストのため意図的に失敗させた");

        var question = "引き継いだ内容から答えて: この学習者はどんな研修を受けていて、"
            + "直近ではどんな課題に取り組んでいましたか。3行以内で。";

        var dir = Path.GetDirectoryName(file)!;
        var addDirs = config.ExpandedAddDirs.Contains(dir)
            ? config.ExpandedAddDirs
            : [.. config.ExpandedAddDirs, dir];

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = ClaudeSidecar.Instance.AskAsync(config, takeover + "\n" + question,
            Path.GetTempPath(), null, addDirs,
            detail => Console.WriteLine($"   進行: {detail}"), CancellationToken.None)
            .GetAwaiter().GetResult();
        sw.Stop();

        Console.WriteLine($"---- 応答 ({sw.Elapsed.TotalSeconds:0}秒) ----");
        Console.WriteLine(result.Text);
        return 0;
    }

    /// <summary>
    /// キャプチャ画像が無い (テキストだけを送った) 場合でもインクが描けることを検証する。
    /// 実際に「棒グラフを描いて説明する」応答が、図だけ抜け落ちて届いた事例の再現。
    /// </summary>
    private static int InkWithoutCaptureTest(AppConfig config)
    {
        using var onenote = new OneNoteApp();
        var (sectionId, sectionName) = FindRecentSection(onenote);
        Console.WriteLine($"対象セクション: {sectionName}");

        var pageId = onenote.CreateNewPage(sectionId);
        Console.WriteLine($"テストページ作成: {pageId}");
        try
        {
            // 実際に届いた応答と同じ形 (2本の棒 + 赤の差分ブラケット)
            var response = string.Join("\n",
            [
                "上の棒が3か月前、下の棒が今月。",
                "{{ink: 0,10 200,10 200,32 0,32 0,10 | color=#1F4E79 | width=2}}",
                "{{ink: 0,52 140,52 140,74 0,74 0,52 | color=#1F4E79 | width=2}}",
                "{{ink: 140,44 140,84 | color=#D40000 | width=2}}",
                "{{ink: 200,44 200,84 | color=#D40000 | width=2}}",
                "{{ink: 140,84 200,84 | color=#D40000 | width=2}}",
                "赤で挟んだはみ出しが引き算のほう。",
                "{{ink-overlay: 10,10 50,50 | color=#D40000}}",
                "{{ink-overlay: circle 120,60 r=25 | color=#D40000 | width=3}}",
                "{{ink-overlay: wave 20,100 160,100 | color=#D40000 | width=2}}",
                "{{ink-overlay: ? 180,88 size=22 | color=#D40000 | width=2}}",
            ]);

            var parts = ResponseParser.Parse(response);
            var inkParts = parts.OfType<InkPart>().ToList();
            Console.WriteLine($"パート数: {parts.Count} (うちインク {inkParts.Count}: " +
                $"流し込み {inkParts.Count(p => !p.Overlay)} / 重ね書き {inkParts.Count(p => p.Overlay)})");

            var sel = new Selection { BoundsPt = new System.Windows.Rect(72, 100, 300, 20) };
            foreach (var part in parts)
            {
                var anchor = PageXml.ComputeInsertAnchor(onenote.GetPageXmlBasic(pageId), sel, belowAll: true);
                // captureMap を null にする = 画像を送っていない状況
                onenote.UpdatePage(PageXml.BuildResponseXml(pageId, anchor, [part], config.ResponseColor, null));
                System.Threading.Thread.Sleep(400);
            }

            var final = System.Xml.Linq.XDocument.Parse(onenote.GetPageXmlBasic(pageId));
            var inkCount = final.Descendants(PageXml.One + "InkDrawing").Count();
            Console.WriteLine($"読み戻し: InkDrawing={inkCount} (期待: 1 以上。重ね書きは無視されるのが正しい)");

            var ok = inkCount >= 1;
            Console.WriteLine(ok ? "OK: 画像が無くてもインクが描けた" : "NG: インクが描かれていない");
            return ok ? 0 : 1;
        }
        finally
        {
            onenote.DeleteHierarchyItem(pageId);
            Console.WriteLine("テストページを削除しました (ノートブックのごみ箱に移動)");
        }
    }

    /// <summary>
    /// 音声入力のプロンプトに、書かれた内容が実際に入るかを確認する。
    /// テキストが丸ごと落ちていた不具合の再発防止。
    /// </summary>
    private static int VoicePromptTest(AppConfig config)
    {
        using var onenote = new OneNoteApp();
        var (pageId, sectionId) = onenote.GetCurrentContext();
        if (string.IsNullOrEmpty(pageId)) { Console.WriteLine("ページが開かれていません"); return 1; }

        var cfg = config.Profiles.Length > 0 && !string.IsNullOrEmpty(sectionId)
            ? config.ResolveForSection(onenote.GetSectionName(sectionId), out _)
            : config;

        var snap = PageSnapshot.FromXml(onenote.GetPageXmlBasic(pageId));
        var changed = snap.ChangesSince(BaselineStore.Load(pageId));
        var sel = PageSnapshot.BuildSelection(onenote.GetPageXml(pageId),
            changed.Select(c => c.Object.ObjectId).ToHashSet());
        Console.WriteLine($"差分: 手書き={sel.Ink.Count} 画像={sel.Images.Count} テキスト={sel.Text.Length}文字");

        // 差分が無いときは、配線が正しいかを合成テキストで確かめる
        if (string.IsNullOrWhiteSpace(sel.Text))
        {
            sel.Text = "想定投資 1人月 × 80万円 = 80万円 / ROI = 営業利益 ÷ 投資 = 2137.5%";
            Console.WriteLine($"(差分が無いので合成テキストで検証: {sel.Text.Length}文字)");
        }

        // AskFlow.RunVoiceAsync と同じ組み立て
        var writingParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(sel.Text))
            writingParts.Add($"前回から新しく書かれたテキスト:\n---\n{sel.Text}\n---");
        var voiceWriting = writingParts.Count > 0
            ? string.Join("\n", writingParts)
            : "（前回から新しく書かれたものはありません。発言だけで答えてください）";

        var prompt = cfg.VoicePromptTemplateText
            .Replace("{voice}", "これで合ってるか見て")
            .Replace("{voiceWriting}", voiceWriting)
            .Replace("{image}", "")
            .Replace("{figureGuide}", cfg.FigureGuideText);

        var included = !string.IsNullOrWhiteSpace(sel.Text) && prompt.Contains(sel.Text);
        Console.WriteLine();
        Console.WriteLine("---- プロンプトの該当箇所 ----");
        var idx = prompt.IndexOf("新しく書かれたテキスト", StringComparison.Ordinal);
        Console.WriteLine(idx >= 0
            ? prompt.Substring(idx, Math.Min(300, prompt.Length - idx))
            : "(テキストの記載なし)");
        Console.WriteLine();
        Console.WriteLine(sel.Text.Length == 0
            ? "テキストの差分が無いため判定不能 (ノートに文字を書いてから再実行してください)"
            : included ? "OK: 書かれたテキストがプロンプトに含まれています" : "NG: 書かれたテキストが落ちています");
        return sel.Text.Length == 0 ? 0 : (included ? 0 : 1);
    }

    /// <summary>
    /// キャプチャ処理のどこに時間がかかっているかを段階ごとに計測する。
    /// 「押してから固まる」の原因を特定するため。
    /// </summary>
    private static int BenchCapture(AppConfig config)
    {
        using var onenote = new OneNoteApp();
        var pageId = onenote.GetCurrentPageId();
        if (string.IsNullOrEmpty(pageId))
        {
            Console.WriteLine("OneNote でページが開かれていません");
            return 1;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        double Lap(string label)
        {
            var ms = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"{label,-40} {ms,8:N0} ms");
            sw.Restart();
            return ms;
        }

        var lightXml = onenote.GetPageXmlBasic(pageId);
        Lap($"1) 軽い XML を取得 ({lightXml.Length / 1024.0 / 1024.0:0.0} MB)");

        var snapshot = PageSnapshot.FromXml(lightXml);
        Lap($"2) 差分用の解析 (オブジェクト {snapshot.Objects.Count} 個)");

        var baseline = BaselineStore.Load(pageId);
        var changes = snapshot.ChangesSince(baseline);
        var inkChanges = changes.Count(c => !c.Object.IsText);
        Lap($"3) 基準と突き合わせ (差分 {changes.Count} 個 / うち手書き {inkChanges})");

        if (baseline == null)
            Console.WriteLine("   (このページの基準がまだ無いので、全件が差分として出ています)");
        if (inkChanges == 0)
        {
            Console.WriteLine("手書きの差分が無いため、ここまで。");
            return 0;
        }

        var fullXml = onenote.GetPageXml(pageId);
        Lap($"4) ISF込みで取得 ({fullXml.Length / 1024.0 / 1024.0:0.0} MB)");

        var sel = PageSnapshot.BuildSelection(fullXml, changes.Select(c => c.Object.ObjectId).ToHashSet());
        Lap($"5) 差分ぶんの取り出し (ink={sel.Ink.Count})");

        var outPng = Path.Combine(Path.GetTempPath(), "claudenote-bench.png");
        var render = SelectionRenderer.RenderToPng(sel, outPng, config.CaptureBackground);
        Lap($"6) 描画 ({render?.WidthPx}x{render?.HeightPx}px)");

        Console.WriteLine();
        Console.WriteLine($"ページ全体 {snapshot.Objects.Count} 個 → 送るのは {sel.Ink.Count} 個");
        Console.WriteLine($"画像: {outPng}");
        return 0;
    }

    /// <summary>
    /// 応答テキストの横幅が設定どおりになるかをテストページで確認する。
    /// 選択範囲の幅に引きずられて行長が変わっていた問題の検証用。
    /// </summary>
    private static int WidthTest(AppConfig config)
    {
        using var onenote = new OneNoteApp();
        var (sectionId, sectionName) = FindRecentSection(onenote);
        Console.WriteLine($"対象セクション: {sectionName}");
        Console.WriteLine($"設定: {config.ResponseWidthChars}文字 × {config.ResponseCharWidthPt}pt = " +
            $"{config.ResponseWidthPt:0.#}pt");

        var pageId = onenote.CreateNewPage(sectionId);
        Console.WriteLine($"テストページ作成: {pageId}");
        try
        {
            // 33〜37文字の行をそれぞれ別の段落として入れ、高さから折り返しの有無を見る
            var kana = string.Concat(Enumerable.Repeat("あいうえおかきくけこさしすせそたちつてとなにぬねのはひふへほまみむめも", 2));
            var counts = new[] { 33, 34, 35, 36, 37 };
            foreach (var n in counts)
            {
                var line = kana.Substring(0, n);
                var anchor = PageXml.ComputeInsertAnchor(onenote.GetPageXmlBasic(pageId),
                    new Selection { BoundsPt = new System.Windows.Rect(72, 100, 700, 20) }, belowAll: true);
                onenote.UpdatePage(PageXml.BuildResponseXml(pageId, anchor,
                    [new TextPart(line)], config.ResponseColor, null, config.ResponseWidthPt));
                System.Threading.Thread.Sleep(400);
            }

            var doc = System.Xml.Linq.XDocument.Parse(onenote.GetPageXmlBasic(pageId));
            var one = PageXml.One;
            var outlines = doc.Root!.Elements(one + "Outline")
                .Select(o => o.Element(one + "Size"))
                .Where(sz => sz != null)
                .Select(sz => (
                    W: double.Parse((string)sz!.Attribute("width")!, System.Globalization.CultureInfo.InvariantCulture),
                    H: double.Parse((string)sz!.Attribute("height")!, System.Globalization.CultureInfo.InvariantCulture)))
                .ToList();

            Console.WriteLine();
            var expected = config.ResponseWidthPt ?? 0;
            var widthOk = outlines.All(o => Math.Abs(o.W - expected) < 1.0);
            for (var i = 0; i < outlines.Count && i < counts.Length; i++)
            {
                var wrapped = outlines[i].H > 26;   // 1行なら概ね 15〜20pt
                Console.WriteLine($"  {counts[i]}文字: " +
                    $"幅={outlines[i].W:0.#}pt 高さ={outlines[i].H:0.#}pt → {(wrapped ? "折り返した" : "1行に収まった")}");
            }
            Console.WriteLine();
            Console.WriteLine(widthOk
                ? $"OK: すべて設定どおりの {expected:0.#}pt 幅 (選択範囲の 700pt に引きずられていない)"
                : "NG: 幅が設定どおりではありません");
            var ok = widthOk;
            return ok ? 0 : 1;
        }
        finally
        {
            onenote.DeleteHierarchyItem(pageId);
            Console.WriteLine("テストページを削除しました (ノートブックのごみ箱に移動)");
        }
    }

    /// <summary>
    /// ノートの背景色の判定結果を表示する。
    /// OneNote の「表示 → 背景色の切り替え」を切り替えて実行し直すと、値が追従するか確かめられる。
    /// </summary>
    private static int ThemeTest(AppConfig config)
    {
        Console.WriteLine($"設定 noteTheme: {config.NoteTheme}");
        Console.WriteLine($"判定: {(config.IsDarkNote ? "暗い背景 (ダークモード)" : "白い背景")}");
        Console.WriteLine($"自動判定の生の結果: {(NoteThemeDetector.IsDarkCanvas() ? "暗い" : "白い")}");
        Console.WriteLine();
        Console.WriteLine("--- Claude に渡している図の指針 (背景に関する行) ---");
        foreach (var line in config.FigureGuideText.Split('\n'))
        {
            if (line.Contains("背景") || line.Contains("ink を優先")) Console.WriteLine("  " + line);
        }
        return 0;
    }

    /// <summary>ボタンの各状態を画像に描き出して見た目を確認する。</summary>
    private static int ButtonPreview(AppConfig config)
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        var size = Math.Max(config.FloatButtonSize, 32);
        var outDir = Path.Combine(Path.GetTempPath(), "claudenote-button");
        Directory.CreateDirectory(outDir);

        var states = new (string Name, Action<FloatButtonForm> Setup)[]
        {
            ("1-通常", _ => { }),
            ("2-処理中", b => b.SetBusy(true)),
            ("3-成功", b => b.Flash(true, "ノートに挿入しました")),
            ("4-警告", b => b.Flash(false, "まだ何も書かれていません")),
        };

        foreach (var (name, setup) in states)
        {
            using var form = new FloatButtonForm(size, () => { });
            form.Show();
            setup(form);
            System.Windows.Forms.Application.DoEvents();

            using var bmp = new System.Drawing.Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height));
            var path = Path.Combine(outDir, name + ".png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"{name}: {path}");
            form.Hide();
        }
        return 0;
    }


    /// <summary>
    /// 「実行 → 途中でキャンセル → すぐ次を実行」が正しく回るかを検証する。
    /// キャンセルがサイドカーに届かないと前の要求が走り続け、次の要求が返らなくなる。
    /// </summary>
    private static int CancelTest(AppConfig config)
    {
        var cwd = Path.GetTempPath();
        var sidecar = ClaudeSidecar.Instance;

        Console.WriteLine("1) 長めの依頼を投げて 8 秒後にキャンセルします");
        using var cts = new CancellationTokenSource();
        var first = sidecar.AskAsync(config, "1 から 200 までの素数を1つずつ理由を添えて丁寧に説明して。長くて構わない。",
            cwd, null, [], detail => Console.WriteLine($"   進行: {detail}"), cts.Token);
        System.Threading.Thread.Sleep(8000);
        cts.Cancel();
        try
        {
            first.GetAwaiter().GetResult();
            Console.WriteLine("   NG: キャンセルしたのに完了しました");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("   キャンセルされました");
        }

        Console.WriteLine("2) 続けて次の依頼を投げます (前の要求が残っていると返ってきません)");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var second = sidecar.AskAsync(config, "「つながった」とだけ出力して。それ以外は何も書かないで。",
                cwd, null, [], null, CancellationToken.None).GetAwaiter().GetResult();
            sw.Stop();
            Console.WriteLine($"   応答 ({sw.Elapsed.TotalSeconds:0.0}秒): {second.Text}");
            Console.WriteLine("OK: キャンセル後も次の要求が通りました");
            return 0;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"   NG ({sw.Elapsed.TotalSeconds:0.0}秒): {ex.Message}");
            return 1;
        }
    }

    private static int RecordTest(AppConfig config, int seconds)
    {
        var wav = Path.Combine(Path.GetTempPath(), "claudenote-record-test.wav");
        using var recorder = new AudioRecorder();
        Console.WriteLine($"{seconds} 秒間録音します。話しかけてください…");
        recorder.Start(wav, config.AudioDevice, seconds + 5);
        System.Threading.Thread.Sleep(seconds * 1000);
        var rec = recorder.Stop();
        if (rec == null) { Console.WriteLine("録音できませんでした"); return 1; }
        Console.WriteLine($"録音: {rec.Duration.TotalSeconds:0.0}秒 peak={rec.PeakLevel:0.000} → {rec.WavPath}");
        if (rec.PeakLevel < 0.02) Console.WriteLine("※ ほぼ無音です。マイクを確認してください");
        return SttTest(config, rec.WavPath, null);
    }

    private static int SttTest(AppConfig config, string wavPath, string? engineOverride)
    {
        if (engineOverride != null) config.SttEngine = engineOverride;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var text = Transcriber.TranscribeAsync(config, Path.GetFullPath(wavPath)).GetAwaiter().GetResult();
        sw.Stop();
        Console.WriteLine($"engine={config.SttEngine} 所要 {sw.Elapsed.TotalSeconds:0.0} 秒");
        Console.WriteLine("---- 文字起こし ----");
        Console.WriteLine(text);
        return string.IsNullOrWhiteSpace(text) ? 1 : 0;
    }

    /// <summary>
    /// 音声入力の 2 段階挿入を検証する。吹き出しを入れ、その実際の位置を読み直し、
    /// 回答が確実にその下へ入ることを確かめる。マイクは使わない。
    /// </summary>
    private static int VoiceInsertTest(AppConfig config)
    {
        using var onenote = new OneNoteApp();
        var (sectionId, sectionName) = FindRecentSection(onenote);
        Console.WriteLine($"対象セクション: {sectionName}");

        var pageId = onenote.CreateNewPage(sectionId);
        Console.WriteLine($"テストページ作成: {pageId}");
        try
        {
            var voiceText = "この三角形の面積はどうやって求めるの";

            // 既存の内容を模した「じゃまな」アウトラインを 2 つ置く。
            // 選択範囲より下にもあるので、真下に入れる方式だと必ず重なる配置
            onenote.UpdatePage(PageXml.BuildResponseXml(pageId, new System.Windows.Rect(72, 100, 300, 0),
                [new TextPart("既存の内容A")], "#888888", null));
            onenote.UpdatePage(PageXml.BuildResponseXml(pageId, new System.Windows.Rect(72, 300, 300, 0),
                [new TextPart("既存の内容B (これより下が空白)")], "#888888", null));
            System.Threading.Thread.Sleep(800);

            // 選択範囲は上の方 (既存の内容A のあたり) にあると仮定する
            var pageXml = onenote.GetPageXml(pageId);
            var contentBottom = PageXml.ComputeContentBottom(pageXml);
            Console.WriteLine($"ページ全体の下端: {contentBottom:0.#}");

            var sel = new Selection { BoundsPt = new System.Windows.Rect(120, 110, 200, 20) };
            var anchor = PageXml.ComputeInsertAnchor(pageXml, sel, belowAll: true);
            Console.WriteLine($"算出した挿入位置: x={anchor.X:0.#} y={anchor.Bottom:0.#} (選択の左端={sel.BoundsPt?.X:0.#})");
            if (Math.Abs(anchor.X - 120) > 0.1)
            {
                Console.WriteLine("NG: x が選択範囲の左端に揃っていません");
                return 1;
            }
            if (contentBottom is double cb && anchor.Bottom < cb - 0.1)
            {
                Console.WriteLine("NG: y がページ下端より上です (重なる位置)");
                return 1;
            }

            // 1 段階目: 吹き出し
            var bubble = config.VoicePrefix + voiceText;
            onenote.UpdatePage(PageXml.BuildResponseXml(pageId, anchor, [new TextPart(bubble)], config.VoiceColor, null));
            System.Threading.Thread.Sleep(800);

            var bubbleRect = PageXml.FindOutlineByText(onenote.GetPageXml(pageId), voiceText);
            if (bubbleRect is not System.Windows.Rect br)
            {
                Console.WriteLine("NG: 挿入した吹き出しを見つけられませんでした");
                return 1;
            }
            Console.WriteLine($"吹き出しの実位置: x={br.X:0.#} y={br.Y:0.#} h={br.Height:0.#}");

            // 2 段階目: 同じ規則で計算し直すと、吹き出しが最下部なので回答はその下に入る
            var answerAnchor = PageXml.ComputeInsertAnchor(onenote.GetPageXml(pageId), sel, belowAll: true);
            onenote.UpdatePage(PageXml.BuildResponseXml(pageId, answerAnchor,
                [new TextPart("底辺かける高さわる2だよ。まず底辺がどれか探してみて。")], config.ResponseColor, null));
            System.Threading.Thread.Sleep(800);

            // 順序の検証: 回答が吹き出しより下にあること
            var final = onenote.GetPageXml(pageId);
            var bubbleFinal = PageXml.FindOutlineByText(final, voiceText);
            var answerFinal = PageXml.FindOutlineByText(final, "底辺かける高さわる2");
            if (bubbleFinal is not System.Windows.Rect b2 || answerFinal is not System.Windows.Rect a2)
            {
                Console.WriteLine($"NG: 読み戻せません (吹き出し={bubbleFinal != null} 回答={answerFinal != null})");
                return 1;
            }
            Console.WriteLine($"吹き出し y={b2.Y:0.#} (下端 {b2.Bottom:0.#}) / 回答 y={a2.Y:0.#}");

            // 既存の内容とも重なっていないことを確かめる
            var existingB = PageXml.FindOutlineByText(final, "これより下が空白");
            var clearsExisting = existingB is not System.Windows.Rect eb || a2.Y >= eb.Bottom - 1;
            var ordered = a2.Y >= b2.Bottom - 1;
            Console.WriteLine($"既存の内容Bの下端={((existingB as System.Windows.Rect?)?.Bottom):0.#} → 回答はその下={clearsExisting}");
            Console.WriteLine(ordered && clearsExisting
                ? "OK: 回答が吹き出しの下、かつ既存の内容より下に入りました"
                : "NG: 回答の位置が重なっています");
            return ordered && clearsExisting ? 0 : 1;
        }
        finally
        {
            onenote.DeleteHierarchyItem(pageId);
            Console.WriteLine("テストページを削除しました (ノートブックのごみ箱に移動)");
        }
    }

    /// <summary>図 (画像 + インク + 補助線) の挿入をテストページで検証する。</summary>
    private static int FigureTest(AppConfig config)
    {
        using var onenote = new OneNoteApp();
        var (sectionId, sectionName) = FindRecentSection(onenote);
        Console.WriteLine($"対象セクション: {sectionName}");

        // 出題用の図を PNG で用意 (家庭教師がスクリプトで作る想定と同じ形)
        var pngPath = Path.Combine(Path.GetTempPath(), "claudenote-figure-test.png");
        MakeTrianglePng(pngPath);

        var pageId = onenote.CreateNewPage(sectionId);
        Console.WriteLine($"テストページ作成: {pageId}");
        try
        {
            var response = string.Join("\n",
            [
                "図形テスト: 下の三角形を見て考えてみよう。",
                "{{image: " + pngPath + " | width=180}}",
                "インクで線分図も描いてみるね。",
                "{{ink: 0,0 200,0 | color=#1F4E79 | width=3}}",
                "{{ink: 0,-8 0,8 | color=#1F4E79 | width=3}}",
                "{{ink: 200,-8 200,8 | color=#1F4E79 | width=3}}",
                "で、何を聞かれてたっけ？",
                "{{ink-overlay: 20,20 120,90 | color=#D40000 | width=2}}",
            ]);

            var parts = ResponseParser.Parse(response);
            Console.WriteLine($"解析結果: {parts.Count} パート " +
                $"(text={parts.Count(p => p is TextPart)}, image={parts.Count(p => p is ImagePart)}, ink={parts.Count(p => p is InkPart)})");

            // 選択範囲を模した仮のキャプチャ座標系 (2倍ズーム・パディング12px)
            var map = new CaptureMap(OriginXPt: 72, OriginYPt: 100, PxPerPt: 96.0 / 72.0 * 2, PadPx: 12);
            var anchor = new System.Windows.Rect(72, 100, 300, 120);
            var xml = PageXml.BuildResponseXml(pageId, anchor, parts, config.ResponseColor, map);
            onenote.UpdatePage(xml);

            System.Threading.Thread.Sleep(1000);
            var readBack = onenote.GetPageXml(pageId);
            var doc = System.Xml.Linq.XDocument.Parse(readBack);
            var inkCount = doc.Descendants(PageXml.One + "InkDrawing").Count();
            var imgCount = doc.Descendants(PageXml.One + "Image").Count();
            var hasText = readBack.Contains("何を聞かれてたっけ");
            Console.WriteLine($"読み戻し: InkDrawing={inkCount} Image={imgCount} text={hasText}");

            // 補助線の座標検証: キャプチャ座標 (20,20) は
            // origin(72,100) + (20 - pad12)/pxPerPt = (75, 103) に来るはず
            var expectedX = map.OriginXPt + (20 - map.PadPx) / map.PxPerPt;
            var expectedY = map.OriginYPt + (20 - map.PadPx) / map.PxPerPt;
            var overlayOk = doc.Descendants(PageXml.One + "InkDrawing")
                .Select(d => d.Element(PageXml.One + "Position"))
                .Any(p => p != null
                    && double.TryParse((string?)p.Attribute("x"), out var px)
                    && double.TryParse((string?)p.Attribute("y"), out var py)
                    && Math.Abs(px - expectedX) < 3 && Math.Abs(py - expectedY) < 3);
            Console.WriteLine($"補助線の座標: 期待 ({expectedX:0.#}, {expectedY:0.#}) → 一致={overlayOk}");
            foreach (var pos in doc.Descendants(PageXml.One + "InkDrawing").Select(d => d.Element(PageXml.One + "Position")))
                Console.WriteLine($"  実際の InkDrawing 位置: x={(string?)pos?.Attribute("x")} y={(string?)pos?.Attribute("y")}");

            var ok = inkCount >= 2 && imgCount >= 1 && hasText && overlayOk;
            Console.WriteLine(ok ? "OK: 図の挿入に成功" : "NG: 期待した要素が読み戻せません");
            return ok ? 0 : 1;
        }
        finally
        {
            onenote.DeleteHierarchyItem(pageId);
            Console.WriteLine("テストページを削除しました (ノートブックのごみ箱に移動)");
            try { File.Delete(pngPath); } catch { }
        }
    }

    private static void MakeTrianglePng(string path)
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var geo = new System.Windows.Media.StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new System.Windows.Point(10, 110), false, true);
                ctx.LineTo(new System.Windows.Point(150, 110), true, true);
                ctx.LineTo(new System.Windows.Point(80, 10), true, true);
            }
            dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new System.Windows.Rect(0, 0, 160, 120));
            dc.DrawGeometry(null, new System.Windows.Media.Pen(System.Windows.Media.Brushes.Black, 2), geo);
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(160, 120, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>直近に編集された通常セクションを探す (ごみ箱・削除済みページは除外)。</summary>
    private static (string SectionId, string SectionName) FindRecentSection(OneNoteApp onenote)
    {
        var hier = System.Xml.Linq.XDocument.Parse(onenote.GetHierarchyXml());
        var one = PageXml.One;

        static bool IsUsable(System.Xml.Linq.XElement page, System.Xml.Linq.XNamespace one)
        {
            if ((string?)page.Attribute("isInRecycleBin") == "true") return false;
            return !page.Ancestors(one + "Section").Any(s =>
                (string?)s.Attribute("isInRecycleBin") == "true" ||
                (string?)s.Attribute("isRecycleBin") == "true" ||
                (string?)s.Attribute("isDeletedPages") == "true");
        }

        var recentPage = hier.Descendants(one + "Page")
            .Where(p => IsUsable(p, one))
            .OrderByDescending(p => (string?)p.Attribute("lastModifiedTime") ?? "")
            .FirstOrDefault() ?? throw new InvalidOperationException("ページが見つかりません");
        var section = recentPage.Ancestors(one + "Section").First();
        return ((string?)section.Attribute("ID") ?? throw new InvalidOperationException("セクション ID が取れません"),
                (string?)section.Attribute("name") ?? "");
    }

    private static int InsertTest(AppConfig config)
    {
        using var onenote = new OneNoteApp();

        // 直近に編集されたページのセクションにテストページを作る (終わったら削除)
        var hier = System.Xml.Linq.XDocument.Parse(onenote.GetHierarchyXml());
        var one = PageXml.One;
        var recentPage = hier.Descendants(one + "Page")
            .OrderByDescending(p => (string?)p.Attribute("lastModifiedTime") ?? "")
            .FirstOrDefault() ?? throw new InvalidOperationException("ページが見つかりません");
        var sectionId = (string?)recentPage.Ancestors(one + "Section").First().Attribute("ID")
            ?? throw new InvalidOperationException("セクション ID が取れません");

        var pageId = onenote.CreateNewPage(sectionId);
        Console.WriteLine($"テストページ作成: {pageId}");
        try
        {
            var anchor = new System.Windows.Rect(72, 90, 300, 40);
            var xml = PageXml.BuildResponseXml(pageId, anchor,
                "ClaudeNote 挿入テスト 1行目\n2行目 (日本語・記号 <>&' テスト)\n\n4行目", config.ResponseColor);
            onenote.UpdatePage(xml);

            var readBack = onenote.GetPageXml(pageId);
            var ok = readBack.Contains("挿入テスト") && readBack.Contains("4行目");
            Console.WriteLine(ok ? "OK: 挿入と読み戻しに成功" : "NG: 挿入した内容が読み戻せません");
            return ok ? 0 : 1;
        }
        finally
        {
            onenote.DeleteHierarchyItem(pageId);
            Console.WriteLine("テストページを削除しました (ノートブックのごみ箱に移動)");
        }
    }
}
