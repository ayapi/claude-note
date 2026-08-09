using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
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
    public static string BuildResponseXml(string pageId, Rect anchorPt, string responseText, string colorHex)
    {
        var inv = CultureInfo.InvariantCulture;
        var x = Math.Max(anchorPt.X, 0);
        var y = anchorPt.Bottom + 12;
        var width = Math.Max(anchorPt.Width, 240);

        var lines = responseText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var oes = lines.Select(line =>
            new XElement(One + "OE",
                new XElement(One + "T", new XCData(WrapLine(line, colorHex)))));

        var pageEl = new XElement(One + "Page",
            new XAttribute(XNamespace.Xmlns + "one", One.NamespaceName),
            new XAttribute("ID", pageId),
            new XElement(One + "Outline",
                new XElement(One + "Position",
                    new XAttribute("x", x.ToString("0.##", inv)),
                    new XAttribute("y", y.ToString("0.##", inv))),
                new XElement(One + "Size",
                    new XAttribute("width", width.ToString("0.##", inv)),
                    new XAttribute("height", "20"),
                    new XAttribute("isSetByUser", "true")),
                new XElement(One + "OEChildren", oes)));

        return pageEl.ToString(SaveOptions.DisableFormatting);
    }

    private static string WrapLine(string line, string colorHex)
    {
        if (line.Length == 0) return "";
        var escaped = WebUtility.HtmlEncode(line);
        return $"<span style='color:{colorHex}'>{escaped}</span>";
    }
}
