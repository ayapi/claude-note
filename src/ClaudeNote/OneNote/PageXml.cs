using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace ClaudeNote;

public sealed record InkItem(byte[] Isf, Rect? PageRectPt);

public sealed record ImageItem(byte[] Data, Rect? PageRectPt);

public sealed class Selection
{
    public string PageId = "";
    public List<InkItem> Ink { get; } = [];
    public List<ImageItem> Images { get; } = [];
    public string Text = "";

    /// <summary>選択範囲全体の外接矩形 (pt、ページ座標)。位置情報が一切取れなければ null。</summary>
    public Rect? BoundsPt;

    /// <summary>ページ内の全要素の下端など、挿入位置のフォールバック (pt)。</summary>
    public Rect? FallbackBoundsPt;

    public bool IsEmpty => Ink.Count == 0 && Images.Count == 0 && string.IsNullOrWhiteSpace(Text);
    public bool HasVisual => Ink.Count > 0 || Images.Count > 0;
}

public static class PageXml
{
    public static readonly XNamespace One = "http://schemas.microsoft.com/office/onenote/2013/onenote";

    private static readonly Regex TagPattern = new("<[^>]+>", RegexOptions.Compiled);

    public static Selection ParseSelection(string pageXml) => Parse(pageXml, selectedOnly: true);

    /// <summary>デバッグ用: ページ内の全 ink / 画像を選択扱いで取り出す。</summary>
    public static Selection ParseAll(string pageXml) => Parse(pageXml, selectedOnly: false);

    private static Selection Parse(string pageXml, bool selectedOnly)
    {
        var doc = XDocument.Parse(pageXml);
        var page = doc.Root ?? throw new UserFacingException("ページ XML を解析できませんでした。");

        var sel = new Selection { PageId = (string?)page.Attribute("ID") ?? "" };
        var textParts = new List<string>();

        foreach (var el in page.Descendants())
        {
            if (el.Ancestors(One + "Title").Any()) continue;

            var name = el.Name.LocalName;
            var isSelected = !selectedOnly || IsSelected(el) || el.Ancestors().Any(IsSelected);

            switch (name)
            {
                case "InkDrawing":
                case "InkWord":
                    if (!isSelected) break;
                    var isf = ReadData(el);
                    if (isf != null) sel.Ink.Add(new InkItem(isf, ReadRect(el)));
                    break;

                case "Image":
                    if (!isSelected) break;
                    var img = ReadData(el);
                    if (img != null) sel.Images.Add(new ImageItem(img, ReadRect(el)));
                    break;

                case "T":
                    if (!isSelected) break;
                    var text = WebUtility.HtmlDecode(TagPattern.Replace(el.Value, ""));
                    if (!string.IsNullOrWhiteSpace(text)) textParts.Add(text.Trim());
                    break;
            }
        }

        sel.Text = string.Join("\n", textParts);
        sel.BoundsPt = ComputeSelectionBounds(sel, page, selectedOnly);
        sel.FallbackBoundsPt = ComputePageContentBounds(page);
        return sel;
    }

    private static bool IsSelected(XElement el)
    {
        var s = (string?)el.Attribute("selected");
        return s is "all" or "partial";
    }

    private static byte[]? ReadData(XElement el)
    {
        var data = el.Element(One + "Data")?.Value;
        if (string.IsNullOrWhiteSpace(data)) return null;
        try { return Convert.FromBase64String(data.Trim()); }
        catch { return null; }
    }

    private static Rect? ReadRect(XElement el)
    {
        var pos = el.Element(One + "Position");
        var size = el.Element(One + "Size");
        if (pos == null || size == null) return null;
        if (!TryAttr(pos, "x", out var x) || !TryAttr(pos, "y", out var y)) return null;
        if (!TryAttr(size, "width", out var w) || !TryAttr(size, "height", out var h)) return null;
        return new Rect(x, y, Math.Max(w, 0.01), Math.Max(h, 0.01));
    }

    private static bool TryAttr(XElement el, string name, out double value) =>
        double.TryParse((string?)el.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static Rect? ComputeSelectionBounds(Selection sel, XElement page, bool selectedOnly)
    {
        Rect? bounds = null;

        foreach (var r in sel.Ink.Select(i => i.PageRectPt).Concat(sel.Images.Select(i => i.PageRectPt)))
        {
            if (r is not Rect rect) continue;
            bounds = bounds is Rect b ? Rect.Union(b, rect) : rect;
        }
        if (bounds != null) return bounds;

        // ink/画像に位置が無い、またはテキストのみの選択: 選択要素を含む Outline の矩形を使う
        foreach (var outline in page.Elements(One + "Outline"))
        {
            var contains = !selectedOnly
                || IsSelected(outline)
                || outline.Descendants().Any(IsSelected);
            if (!contains) continue;
            if (ReadRect(outline) is Rect r)
                bounds = bounds is Rect b ? Rect.Union(b, r) : r;
        }
        return bounds;
    }

    /// <summary>ページ直下の位置付き要素全体の外接矩形。挿入位置の最終フォールバック。</summary>
    private static Rect? ComputePageContentBounds(XElement page)
    {
        Rect? bounds = null;
        foreach (var el in page.Elements())
        {
            if (ReadRect(el) is Rect r)
                bounds = bounds is Rect b ? Rect.Union(b, r) : r;
        }
        return bounds;
    }

    /// <summary>応答テキストを選択範囲の真下に挿入するための UpdatePageContent 用 XML を組み立てる。</summary>
    public static string BuildResponseXml(string pageId, Rect anchorPt, string responseText, string colorHex) =>
        BuildResponseXml(pageId, anchorPt, [new TextPart(responseText)], colorHex, null);

    /// <summary>
    /// テキスト・画像・インクが混在した応答を、選択範囲の真下に配置する XML を組み立てる。
    /// ink-overlay は選択範囲そのものに重ねる (補助線)。
    /// </summary>
    /// <param name="captureMap">
    /// キャプチャ画像のピクセル座標 → ページ座標 (pt) の変換。インク指定に使う。null ならインクは無視。
    /// </param>
    public static string BuildResponseXml(string pageId, Rect anchorPt, IReadOnlyList<ResponsePart> parts,
        string colorHex, CaptureMap? captureMap)
    {
        var inv = CultureInfo.InvariantCulture;
        var x = Math.Max(anchorPt.X, 0);
        var cursorY = anchorPt.Bottom + 12;
        var width = Math.Max(anchorPt.Width, 240);

        var pageEl = new XElement(One + "Page",
            new XAttribute(XNamespace.Xmlns + "one", One.NamespaceName),
            new XAttribute("ID", pageId));

        // 連続するテキストは1つのアウトラインにまとめる
        var textBuffer = new List<string>();
        void FlushText()
        {
            if (textBuffer.Count == 0) return;
            var oes = textBuffer.Select(line =>
                new XElement(One + "OE",
                    new XElement(One + "T", new XCData(WrapLine(line, colorHex)))));
            pageEl.Add(new XElement(One + "Outline",
                new XElement(One + "Position",
                    new XAttribute("x", x.ToString("0.##", inv)),
                    new XAttribute("y", cursorY.ToString("0.##", inv))),
                new XElement(One + "Size",
                    new XAttribute("width", width.ToString("0.##", inv)),
                    new XAttribute("height", "20"),
                    new XAttribute("isSetByUser", "true")),
                new XElement(One + "OEChildren", oes)));
            // 行数から高さを見積もってカーソルを進める (OneNote が実寸に再配置する)
            cursorY += textBuffer.Count * 14 + 12;
            textBuffer.Clear();
        }

        foreach (var part in parts)
        {
            switch (part)
            {
                case TextPart t:
                    textBuffer.AddRange(t.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
                    break;

                case ImagePart img:
                    FlushText();
                    if (AppendImage(pageEl, img, x, cursorY, out var imgHeight))
                        cursorY += imgHeight + 12;
                    break;

                case InkPart ink when captureMap != null:
                    FlushText();
                    if (ink.Overlay)
                    {
                        AppendInk(pageEl, ink, captureMap, overlayOrigin: true, x, cursorY, out _);
                    }
                    else if (AppendInk(pageEl, ink, captureMap, overlayOrigin: false, x, cursorY, out var inkHeight))
                    {
                        cursorY += inkHeight + 12;
                    }
                    break;
            }
        }
        FlushText();

        return pageEl.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// ページ上のすべての要素の下端 (pt)。ここより下は空白なので、
    /// 回答をここに置けば既存の内容と重ならない。要素が無ければ null。
    /// </summary>
    public static double? ComputeContentBottom(string pageXml)
    {
        var page = XDocument.Parse(pageXml).Root;
        if (page == null) return null;

        double? bottom = null;
        foreach (var el in page.Elements())
        {
            if (ReadRect(el) is not Rect r) continue;
            if (bottom is not double b || r.Bottom > b) bottom = r.Bottom;
        }
        return bottom;
    }

    /// <summary>
    /// 回答の挿入位置を決める。x は選択範囲の左端に揃え、y はページ全体の下端
    /// (空白部分) にすることで、既存の内容と重ならないようにする。
    /// </summary>
    public static Rect ComputeInsertAnchor(string pageXml, Selection sel, bool belowAll)
    {
        var selRect = sel.BoundsPt ?? sel.FallbackBoundsPt ?? new Rect(72, 72, 240, 20);
        if (!belowAll) return selRect;

        var contentBottom = ComputeContentBottom(pageXml);
        // 選択範囲より上には置かない (空のページや位置が取れない場合の保険)
        var y = contentBottom is double b && b > selRect.Bottom ? b : selRect.Bottom;
        // 高さ 0 の矩形にして、下端 = 挿入の基準線とする
        return new Rect(selRect.X, y, selRect.Width, 0);
    }

    /// <summary>
    /// 直前に挿入したアウトラインを本文の一部で探して、その矩形を返す。
    /// 2 段階挿入 (文字起こし → 回答) で、後続を実際の位置の下に置くために使う。
    /// 見つからなければ null。
    /// </summary>
    public static Rect? FindOutlineByText(string pageXml, string snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet)) return null;
        var normalized = Normalize(snippet);
        if (normalized.Length == 0) return null;

        var page = XDocument.Parse(pageXml).Root;
        if (page == null) return null;

        Rect? best = null;
        foreach (var outline in page.Elements(One + "Outline"))
        {
            var text = Normalize(string.Concat(
                outline.Descendants(One + "T").Select(t => TagPattern.Replace(t.Value, ""))));
            if (text.Length == 0 || !text.Contains(normalized)) continue;
            if (ReadRect(outline) is not Rect r) continue;
            // 同じ文面が複数あるときは一番下のものを採用する (直前に足したものが下にある)
            if (best is not Rect b || r.Bottom > b.Bottom) best = r;
        }
        return best;
    }

    private static string Normalize(string s) =>
        WebUtility.HtmlDecode(s).Replace(" ", "").Replace(" ", "").Replace("\n", "").Replace("\r", "").Trim();

    private static bool AppendImage(XElement pageEl, ImagePart img, double x, double y, out double heightPt)
    {
        heightPt = 0;
        try
        {
            var path = Environment.ExpandEnvironmentVariables(img.Path);
            if (!File.Exists(path))
            {
                Logger.Log($"画像が見つかりません (無視): {path}");
                return false;
            }
            var bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];

            // 画像は 96dpi の DIP として扱い pt に変換 (1pt = 96/72 DIP)
            var naturalWidthPt = frame.PixelWidth * 72.0 / 96.0;
            var naturalHeightPt = frame.PixelHeight * 72.0 / 96.0;
            var widthPt = img.WidthPt ?? Math.Min(naturalWidthPt, 400);
            heightPt = naturalWidthPt > 0 ? widthPt * (naturalHeightPt / naturalWidthPt) : naturalHeightPt;

            var inv = CultureInfo.InvariantCulture;
            var format = Path.GetExtension(path).Trim('.').ToLowerInvariant() switch
            {
                "jpg" or "jpeg" => "jpg",
                "gif" => "gif",
                "bmp" => "bmp",
                _ => "png",
            };
            pageEl.Add(new XElement(One + "Image",
                new XAttribute("format", format),
                new XElement(One + "Position",
                    new XAttribute("x", x.ToString("0.##", inv)),
                    new XAttribute("y", y.ToString("0.##", inv))),
                new XElement(One + "Size",
                    new XAttribute("width", widthPt.ToString("0.##", inv)),
                    new XAttribute("height", heightPt.ToString("0.##", inv)),
                    new XAttribute("isSetByUser", "true")),
                new XElement(One + "Data", Convert.ToBase64String(bytes))));
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"画像の挿入に失敗 (無視): {ex.Message}");
            return false;
        }
    }

    private static bool AppendInk(XElement pageEl, InkPart ink, CaptureMap map, bool overlayOrigin,
        double flowX, double flowY, out double heightPt)
    {
        heightPt = 0;
        try
        {
            var isf = InkBuilder.BuildIsf(ink.Strokes);
            if (isf == null) return false;

            // ISF 内部座標 (= キャプチャのピクセル座標) の外接矩形を pt に変換
            var boundsPx = InkBuilder.GetBounds(isf);
            var widthPt = boundsPx.Width / map.PxPerPt;
            heightPt = boundsPx.Height / map.PxPerPt;

            double posX, posY;
            if (overlayOrigin)
            {
                // 補助線: キャプチャ画像上の座標をそのまま元の選択範囲の位置へ戻す
                posX = map.OriginXPt + (boundsPx.X - map.PadPx) / map.PxPerPt;
                posY = map.OriginYPt + (boundsPx.Y - map.PadPx) / map.PxPerPt;
            }
            else
            {
                posX = flowX;
                posY = flowY;
            }

            var inv = CultureInfo.InvariantCulture;
            pageEl.Add(new XElement(One + "InkDrawing",
                new XElement(One + "Position",
                    new XAttribute("x", Math.Max(posX, 0).ToString("0.##", inv)),
                    new XAttribute("y", Math.Max(posY, 0).ToString("0.##", inv))),
                new XElement(One + "Size",
                    new XAttribute("width", Math.Max(widthPt, 1).ToString("0.##", inv)),
                    new XAttribute("height", Math.Max(heightPt, 1).ToString("0.##", inv))),
                new XElement(One + "Data", Convert.ToBase64String(isf))));
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"インクの挿入に失敗 (無視): {ex.Message}");
            return false;
        }
    }

    private static string WrapLine(string line, string colorHex)
    {
        if (line.Length == 0) return "";
        var escaped = WebUtility.HtmlEncode(line);
        return $"<span style='color:{colorHex}'>{escaped}</span>";
    }
}
