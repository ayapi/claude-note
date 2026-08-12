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
///   --mic-list                           : 録音デバイスの一覧
///   --record-test [秒]                   : 指定秒だけ録音して文字起こしまで通す
///   --stt-test &lt;wav&gt;                 : 既存の WAV を文字起こしする
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
                case "--selection-test":
                    return SelectionTest(args[1]);
                case "--multipart-test":
                    return MultipartTest(config);
                case "--takeover-test":
                    return TakeoverTest(config, args.Length > 1 ? args[1] : null);
                case "--button-preview":
                    return ButtonPreview(config);
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
        using var onenote = new OneNoteApp();
        var pageId = onenote.GetCurrentPageId();
        if (string.IsNullOrEmpty(pageId))
        {
            Console.WriteLine("OneNote でページが開かれていません");
            return 1;
        }

        // まず軽い取得で選択状態だけ見る (インクの多いページでも速い)
        var quick = PageXml.ParseSelection(onenote.GetPageXmlSelectionOnly(pageId));
        Console.WriteLine($"OneNote の報告: {quick.Diagnostics}");
        Console.WriteLine($"判定: 図={quick.VisualCount} textLen={quick.Text.Length} (軽い XML なので中身はまだ無い)");
        if (!quick.HasVisual)
        {
            Console.WriteLine(quick.IsEmpty
                ? "→ 何も選択されていないと判定"
                : "→ テキストのみ選択と判定 (図は含まれない)");
            return 0;
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
            ("4-警告", b => b.Flash(false, "何も選択されていません")),
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

    /// <summary>保存したページ XML に対して選択判定だけを走らせる (回帰テスト用)。</summary>
    private static int SelectionTest(string xmlPath)
    {
        var sel = PageXml.ParseSelection(File.ReadAllText(xmlPath));
        Console.WriteLine($"図={sel.VisualCount} ink={sel.Ink.Count} images={sel.Images.Count} " +
            $"textLen={sel.Text.Length} bounds={sel.BoundsPt}");
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
