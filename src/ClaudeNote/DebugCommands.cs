using System.IO;
using System.Text;

namespace ClaudeNote;

/// <summary>
/// 動作検証用のコマンドラインモード。
///   --render-test &lt;pageXml&gt; &lt;outPng&gt; : 保存済みページ XML の全 ink/画像を PNG 化
///   --capture-test                       : いま OneNote で選択中の内容をキャプチャして PNG 化 (挿入なし)
///   --ask-test &lt;png&gt; [sessionId]     : PNG を claude CLI に送って応答を表示 (挿入なし)。sessionId 指定で resume 検証
///   --insert-test                        : テストページを作成して挿入 → 検証 → ページ削除
///   --figure-test                        : 図 (画像 + インク) の挿入を検証 → ページ削除
/// </summary>
internal static class DebugCommands
{
    public static int Run(string[] args, AppConfig config)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            switch (args[0])
            {
                case "--render-test":
                    return RenderTest(args[1], args[2]);
                case "--capture-test":
                    return CaptureTest();
                case "--ask-test":
                    return AskTest(config, args[1], args.Length > 2 ? args[2] : null);
                case "--insert-test":
                    return InsertTest(config);
                case "--figure-test":
                    return FigureTest(config);
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
        var onenote = new OneNoteApp();
        var pageId = onenote.GetCurrentPageId();
        if (string.IsNullOrEmpty(pageId))
        {
            Console.WriteLine("OneNote でページが開かれていません");
            return 1;
        }
        var sel = PageXml.ParseSelection(onenote.GetPageXml(pageId));
        Console.WriteLine($"selected: ink={sel.Ink.Count} images={sel.Images.Count} textLen={sel.Text.Length} bounds={sel.BoundsPt}");
        if (!sel.HasVisual)
        {
            Console.WriteLine("ink/画像の選択なし");
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

    /// <summary>図 (画像 + インク + 補助線) の挿入をテストページで検証する。</summary>
    private static int FigureTest(AppConfig config)
    {
        var onenote = new OneNoteApp();
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
        var onenote = new OneNoteApp();

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
