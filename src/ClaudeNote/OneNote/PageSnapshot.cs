using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml.Linq;

namespace ClaudeNote;

/// <summary>
/// ページ上の 1 オブジェクト (手書き / 画像 / 段落)。差分を取るための最小限の情報。
/// 段落 (one:OE) は Position を持たないので Rect は null になることがある。
/// </summary>
public sealed record PageObject(string ObjectId, string Kind, Rect? Rect, string? Text, DateTime? ModifiedAt)
{
    public bool IsText => Kind == "OE";

    /// <summary>
    /// 同じものかを比べるための短い表現。手書き・画像は位置と大きさ、段落は本文のハッシュ。
    /// 保存するのはこれだけでよいので、基準を軽く持ち回せる。
    /// 座標は pt 単位で丸めるため、1pt 未満の揺れは同一とみなされる。
    /// </summary>
    public string Fingerprint => IsText
        ? "t:" + Hash(Text ?? "")
        : Rect is Rect r ? $"r:{r.X:0},{r.Y:0},{r.Width:0},{r.Height:0}" : "r:?";

    private static string Hash(string text)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes, 0, 8);
    }
}

/// <summary>差分の種類。</summary>
public enum PageChangeKind
{
    /// <summary>前回に無かったオブジェクト。</summary>
    Added,
    /// <summary>objectID は同じだが矩形が広がった (併合されたか、描き足された)。</summary>
    Grown,
    /// <summary>段落の本文が書き換わった。</summary>
    Edited,
}

public sealed record PageChange(PageChangeKind Kind, PageObject Object);

/// <summary>
/// 前回送ったときのページの姿。objectID → 指紋 だけを持つので軽い。
/// ページごとに保存しておき、次に送るときの比較対象にする。
/// </summary>
public sealed class PageBaseline
{
    [System.Text.Json.Serialization.JsonPropertyName("pageId")]
    public string PageId { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("takenAt")]
    public DateTime TakenAt { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("fingerprints")]
    public Dictionary<string, string> Fingerprints { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// あるページのオブジェクト一覧。前回の一覧と比べて「新しく書かれたもの」を出すために使う。
///
/// ページ全体の画像を撮って画素を比べる方法もあるが、OneNote のページ XML は
/// 手書きのストロークごと・段落ごとに objectID / lastModifiedTime を持っているので、
/// 画像処理をしなくても集合演算で差分が出せる。表示の拡大率やスクロール位置に
/// 影響されず、座標がそのまま ink-overlay の座標系になるのが利点。
/// </summary>
public sealed class PageSnapshot
{
    public string PageId { get; init; } = "";
    public DateTime TakenAt { get; init; } = DateTime.Now;
    public IReadOnlyDictionary<string, PageObject> Objects { get; init; }
        = new Dictionary<string, PageObject>();

    private static readonly XNamespace One = PageXml.One;
    private static readonly Regex TagPattern = new("<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// ページ XML からスナップショットを作る。バイナリ抜きの XML (GetPageXmlBasic) で足りる。
    /// objectID を持たない要素は差分の対象にできないので飛ばす。
    /// ページタイトルは書き換えの対象ではないので除く。
    /// </summary>
    public static PageSnapshot FromXml(string pageXml)
    {
        var doc = XDocument.Parse(pageXml);
        var page = doc.Root ?? throw new UserFacingException("ページ XML を解析できませんでした。");

        var objects = new Dictionary<string, PageObject>();
        foreach (var el in page.Descendants())
        {
            var kind = el.Name.LocalName;
            if (kind is not ("InkDrawing" or "InkWord" or "Image" or "OE")) continue;
            if (el.Ancestors(One + "Title").Any()) continue;

            var id = (string?)el.Attribute("objectID");
            if (string.IsNullOrEmpty(id)) continue;

            if (kind == "OE")
            {
                // 段落そのものには位置が無い。位置は親のアウトラインが持っている
                var text = ReadText(el);
                if (string.IsNullOrWhiteSpace(text)) continue;
                objects[id] = new PageObject(id, kind, ReadRect(el.Ancestors(One + "Outline").FirstOrDefault()),
                    text, ReadTime(el));
                continue;
            }

            var rect = ReadRect(el);
            if (rect is not Rect r) continue;
            objects[id] = new PageObject(id, kind, r, null, ReadTime(el));
        }

        return new PageSnapshot
        {
            PageId = (string?)page.Attribute("ID") ?? "",
            Objects = objects,
        };
    }

    /// <summary>保存用の基準 (objectID → 指紋)。</summary>
    public PageBaseline ToBaseline() => new()
    {
        PageId = PageId,
        TakenAt = TakenAt,
        Fingerprints = Objects.ToDictionary(kv => kv.Key, kv => kv.Value.Fingerprint),
    };

    /// <summary>
    /// 基準から増えた / 広がった / 書き換わったものを返す。消えたものは対象にしない
    /// (消しゴムで消した跡を送っても仕方がないため)。
    /// 基準が null なら全件を「新規」として返す (そのページで初めて送るとき)。
    /// </summary>
    public IReadOnlyList<PageChange> ChangesSince(PageBaseline? previous)
    {
        var changes = new List<PageChange>();
        foreach (var (id, now) in Objects)
        {
            if (previous == null || !previous.Fingerprints.TryGetValue(id, out var before))
            {
                changes.Add(new PageChange(PageChangeKind.Added, now));
                continue;
            }
            if (string.Equals(now.Fingerprint, before, StringComparison.Ordinal)) continue;

            changes.Add(new PageChange(now.IsText ? PageChangeKind.Edited : PageChangeKind.Grown, now));
        }
        return changes;
    }

    /// <summary>差分の外接矩形。位置を持たないものは寄与しない。差分が無ければ null。</summary>
    public static Rect? BoundsOf(IReadOnlyList<PageChange> changes)
    {
        Rect? bounds = null;
        foreach (var c in changes)
        {
            if (c.Object.Rect is not Rect r) continue;
            bounds = bounds is Rect b ? Rect.Union(b, r) : r;
        }
        return bounds;
    }

    /// <summary>差分に含まれる段落の本文をつなげたもの。手書きだけなら空。</summary>
    public static string TextOf(IReadOnlyList<PageChange> changes) =>
        string.Join("\n", changes
            .Where(c => c.Object.IsText && !string.IsNullOrWhiteSpace(c.Object.Text))
            .OrderBy(c => c.Object.Rect?.Y ?? 0)
            .Select(c => c.Object.Text!.Trim()));

    /// <summary>
    /// 指定した objectID のものだけを集めた Selection を作る。差分の範囲を
    /// そのまま描画して「実際に送られる画像」にするために使う。
    /// バイナリ込みのページ XML (GetPageXml) を渡すこと。
    /// </summary>
    public static Selection BuildSelection(string pageXmlWithBinary, ISet<string> objectIds)
    {
        var doc = XDocument.Parse(pageXmlWithBinary);
        var page = doc.Root ?? throw new UserFacingException("ページ XML を解析できませんでした。");

        var sel = new Selection { PageId = (string?)page.Attribute("ID") ?? "" };
        var texts = new List<string>();
        Rect? bounds = null;

        foreach (var el in page.Descendants())
        {
            var kind = el.Name.LocalName;
            if (kind is not ("InkDrawing" or "InkWord" or "Image" or "OE")) continue;

            var id = (string?)el.Attribute("objectID");
            if (id == null || !objectIds.Contains(id)) continue;

            if (kind == "OE")
            {
                var text = ReadText(el);
                if (!string.IsNullOrWhiteSpace(text)) texts.Add(text.Trim());
                continue;
            }

            var rect = ReadRect(el);
            sel.VisualCount++;
            if (rect is Rect r)
            {
                sel.VisualRects.Add(r);
                bounds = bounds is Rect b ? Rect.Union(b, r) : r;
            }

            var data = ReadData(el);
            if (data == null) continue;
            if (kind == "Image")
            {
                sel.SelectedImageCount++;
                sel.Images.Add(new ImageItem(data, rect));
            }
            else
            {
                sel.SelectedInkCount++;
                sel.Ink.Add(new InkItem(data, rect));
            }
        }

        sel.Text = string.Join("\n", texts);
        sel.BoundsPt = bounds;
        return sel;
    }

    private static string ReadText(XElement oe) =>
        WebUtility.HtmlDecode(TagPattern.Replace(
            string.Concat(oe.Elements(One + "T").Select(t => t.Value)), ""));

    private static byte[]? ReadData(XElement el)
    {
        var data = el.Element(One + "Data")?.Value;
        if (string.IsNullOrWhiteSpace(data)) return null;
        try { return Convert.FromBase64String(data.Trim()); }
        catch { return null; }
    }

    private static Rect? ReadRect(XElement? el)
    {
        if (el == null) return null;
        var pos = el.Element(One + "Position");
        var size = el.Element(One + "Size");
        if (pos == null || size == null) return null;
        if (!TryAttr(pos, "x", out var x) || !TryAttr(pos, "y", out var y)) return null;
        if (!TryAttr(size, "width", out var w) || !TryAttr(size, "height", out var h)) return null;
        return new Rect(x, y, Math.Max(w, 0.01), Math.Max(h, 0.01));
    }

    private static DateTime? ReadTime(XElement el) =>
        DateTime.TryParse((string?)el.Attribute("lastModifiedTime"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static bool TryAttr(XElement el, string name, out double value) =>
        double.TryParse((string?)el.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
