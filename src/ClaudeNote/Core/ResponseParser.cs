using System.Text.RegularExpressions;
using System.Windows.Media;
using Colors = System.Windows.Media.Colors;
using Point = System.Windows.Point;

namespace ClaudeNote;

/// <summary>応答から取り出した挿入物。</summary>
public abstract record ResponsePart;

public sealed record TextPart(string Text) : ResponsePart;

/// <summary>画像ファイルの挿入。Width は pt 指定 (null なら実寸から算出)。</summary>
public sealed record ImagePart(string Path, double? WidthPt) : ResponsePart;

/// <summary>
/// インク描画。座標はキャプチャ画像のピクセル座標系。
/// Overlay=true なら選択範囲に重ねて描く (補助線)。false なら応答の流れの中に置く。
/// </summary>
public sealed record InkPart(InkStroke[] Strokes, bool Overlay) : ResponsePart;

/// <summary>
/// Claude の応答テキストから埋め込みディレクティブを解析する。
///
///   {{image: C:\path\to\figure.png}}          … 画像を挿入 (width=200 で pt 指定可)
///   {{ink: 10,20 40,60 90,20 | color=#D40000 | width=2}}  … 折れ線を1本描く
///   {{ink-overlay: ...}}                      … 選択範囲に重ねて描く (補助線)
///   {{ink-overlay: circle 300,400 r=25}}     … 円 (正解の○)
///   {{ink-overlay: wave 100,300 260,300}}     … 波線 (怪しい途中式の下線)
///   {{ink-overlay: ? 280,290 size=20}}        … 「?」 (波線とセットで使う)
///
/// 複数の ink/ink-overlay 行が連続する場合はまとめて1つの描画にする
/// (図形は複数の線でできているため)。
/// </summary>
public static class ResponseParser
{
    private static readonly Regex Directive = new(
        @"\{\{\s*(?<kind>image|ink-overlay|ink)\s*:\s*(?<body>[^}]*)\}\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<ResponsePart> Parse(string response)
    {
        var parts = new List<ResponsePart>();
        var pendingText = new List<string>();
        var pendingInk = new List<InkStroke>();
        var pendingInkOverlay = new List<InkStroke>();

        void FlushText()
        {
            if (pendingText.Count == 0) return;
            var text = string.Join("\n", pendingText).Trim('\n');
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(new TextPart(text));
            pendingText.Clear();
        }
        void FlushInk()
        {
            if (pendingInk.Count > 0)
            {
                parts.Add(new InkPart([.. pendingInk], Overlay: false));
                pendingInk.Clear();
            }
            if (pendingInkOverlay.Count > 0)
            {
                parts.Add(new InkPart([.. pendingInkOverlay], Overlay: true));
                pendingInkOverlay.Clear();
            }
        }

        foreach (var rawLine in response.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var matches = Directive.Matches(rawLine);
            if (matches.Count == 0)
            {
                FlushInk();
                pendingText.Add(rawLine);
                continue;
            }

            // ディレクティブ行: 行内の残りテキストは (もしあれば) テキストとして残す
            var remainder = Directive.Replace(rawLine, "").Trim();
            if (remainder.Length > 0)
            {
                FlushInk();
                pendingText.Add(remainder);
            }

            foreach (Match m in matches)
            {
                var kind = m.Groups["kind"].Value.ToLowerInvariant();
                var body = m.Groups["body"].Value.Trim();
                if (kind == "image")
                {
                    FlushInk();
                    FlushText();
                    var (path, width) = ParseImageBody(body);
                    if (!string.IsNullOrWhiteSpace(path)) parts.Add(new ImagePart(path, width));
                }
                else
                {
                    FlushText();
                    foreach (var stroke in ParseInkBody(body))
                    {
                        if (kind == "ink-overlay") pendingInkOverlay.Add(stroke);
                        else pendingInk.Add(stroke);
                    }
                }
            }
        }

        FlushInk();
        FlushText();
        return parts;
    }

    private static (string Path, double? WidthPt) ParseImageBody(string body)
    {
        var segments = body.Split('|', StringSplitOptions.TrimEntries);
        var path = segments.Length > 0 ? segments[0].Trim().Trim('"') : "";
        double? width = null;
        foreach (var seg in segments.Skip(1))
        {
            var kv = seg.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length == 2 && kv[0].Equals("width", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(kv[1], out var w) && w > 0)
            {
                width = w;
            }
        }
        return (path, width);
    }

    private static IReadOnlyList<InkStroke> ParseInkBody(string body)
    {
        var segments = body.Split('|', StringSplitOptions.TrimEntries);
        if (segments.Length == 0) return [];

        var color = Colors.Red;
        var width = 2.0;
        // 図形の大きさ (amp / size / r) は第1区画にも「| amp=8」の形にも書けるようにする。
        // どちらで書くかはモデル任せで、片方しか効かないと黙って無視されるため
        double? amount = null;
        foreach (var seg in segments.Skip(1))
        {
            var kv = seg.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;
            if (kv[0].Equals("color", StringComparison.OrdinalIgnoreCase))
                color = InkBuilder.ParseColor(kv[1]);
            else if (kv[0].Equals("width", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(kv[1], out var w) && w > 0)
                width = w;
            else if ((kv[0].Equals("amp", StringComparison.OrdinalIgnoreCase)
                    || kv[0].Equals("size", StringComparison.OrdinalIgnoreCase)
                    || kv[0].Equals("r", StringComparison.OrdinalIgnoreCase))
                && double.TryParse(kv[1], System.Globalization.CultureInfo.InvariantCulture, out var a) && a > 0)
                amount = a;
        }

        // 図形の省略記法 (circle / wave / ?) は複数画になることがある
        var shape = ParseShape(segments[0], amount);
        var strokes = shape ?? [InkBuilder.ParsePoints(segments[0])];
        strokes = [.. strokes.Where(p => p.Length >= 2)];
        if (strokes.Length == 0) return [];

        return [.. strokes.Select(p => new InkStroke(p, color, width))];
    }

    private static readonly Regex Circle = new(
        @"^circle\s+(?<x>-?[\d.]+)\s*,\s*(?<y>-?[\d.]+)(?:\s+r\s*=\s*(?<r>[\d.]+))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Wave = new(
        @"^wave\s+(?<x1>-?[\d.]+)\s*,\s*(?<y1>-?[\d.]+)\s+(?<x2>-?[\d.]+)\s*,\s*(?<y2>-?[\d.]+)(?:\s+amp\s*=\s*(?<amp>[\d.]+))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Question = new(
        @"^\?\s+(?<x>-?[\d.]+)\s*,\s*(?<y>-?[\d.]+)(?:\s+size\s*=\s*(?<size>[\d.]+))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 図形の省略記法を点列に展開する。当てはまらなければ null (ふつうの折れ線として扱う)。
    /// 添削でよく使う形を折れ線で書かせると 20〜30 点並べることになり、モデルが
    /// 面倒がって別の記号で済ませてしまうため、短く書けるようにしてある。
    /// </summary>
    private static Point[][]? ParseShape(string body, double? amount)
    {
        body = body.Trim();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double N(Match m, string g, double fallback = 0) =>
            double.TryParse(m.Groups[g].Value, inv, out var v) ? v : fallback;

        var c = Circle.Match(body);
        if (c.Success)
        {
            var r = amount ?? N(c, "r");
            if (r <= 0) return null;
            return [Arc(N(c, "x"), N(c, "y"), r, -Math.PI / 2, 2 * Math.PI + 0.4, 28)];
        }

        var w = Wave.Match(body);
        if (w.Success) return [WavyLine(N(w, "x1"), N(w, "y1"), N(w, "x2"), N(w, "y2"),
            amount ?? (w.Groups["amp"].Success ? N(w, "amp") : 4))];

        var q = Question.Match(body);
        if (q.Success) return QuestionMark(N(q, "x"), N(q, "y"),
            amount ?? (q.Groups["size"].Success ? N(q, "size") : 20));

        return null;
    }

    /// <summary>円弧。手で描いた丸のように、sweep を 2π より少し大きくして始点を越えて閉じる。</summary>
    private static Point[] Arc(double cx, double cy, double r, double start, double sweep, int steps)
    {
        var pts = new Point[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            var a = start + sweep * i / steps;
            pts[i] = new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }
        return pts;
    }

    /// <summary>2点を結ぶ波線 (怪しい箇所の下線)。振幅は amp、山の間隔はその約 2 倍。</summary>
    private static Point[] WavyLine(double x1, double y1, double x2, double y2, double amp)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1 || amp <= 0) return [new Point(x1, y1), new Point(x2, y2)];

        // 線に垂直な向きへ振る
        var ux = dx / len;
        var uy = dy / len;
        var steps = Math.Clamp((int)Math.Round(len / 2), 8, 200);
        var waves = Math.Max(1, Math.Round(len / (amp * 4)));

        var pts = new Point[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            var offset = amp * Math.Sin(2 * Math.PI * waves * t);
            pts[i] = new Point(x1 + dx * t - uy * offset, y1 + dy * t + ux * offset);
        }
        return pts;
    }

    /// <summary>
    /// 「?」を 2 画で描く。x,y は左上ではなく記号の中心上端 (フックの中心)。
    /// size は全体の高さ。
    /// </summary>
    private static Point[][] QuestionMark(double x, double y, double size)
    {
        if (size <= 0) size = 20;
        var r = size * 0.28;

        // 上のフック: 左上から時計回りに 3/4 周ほど回して、そこから下へ伸ばす
        var hook = Arc(x, y + r, r, Math.PI, Math.PI * 1.35, 14).ToList();
        hook.Add(new Point(x, y + size * 0.62));

        // 下の点: ごく短い線分で打つ
        var dotY = y + size * 0.92;
        Point[] dot = [new Point(x, dotY), new Point(x, dotY + Math.Max(size * 0.06, 1))];
        return [[.. hook], dot];
    }

    /// <summary>ディレクティブを取り除いた、通知バルーン用のプレーンテキスト。</summary>
    public static string StripDirectives(string response) =>
        Directive.Replace(response, "").Trim();
}
