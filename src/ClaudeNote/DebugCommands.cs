using System.IO;
using System.Text;

namespace ClaudeNote;

/// <summary>
/// 動作検証用のコマンドラインモード。
///   --render-test &lt;pageXml&gt; &lt;outPng&gt; : 保存済みページ XML の全 ink/画像を PNG 化
///   --capture-test                       : いま OneNote で選択中の内容をキャプチャして PNG 化 (挿入なし)
///   --ask-test &lt;png&gt; [sessionId]     : PNG を claude CLI に送って応答を表示 (挿入なし)。sessionId 指定で resume 検証
///   --insert-test                        : テストページを作成して挿入 → 検証 → ページ削除
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
