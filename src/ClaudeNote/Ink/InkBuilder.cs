using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace ClaudeNote;

/// <summary>1本のストローク: 折れ線の頂点列 + 色・太さ。</summary>
public sealed record InkStroke(Point[] Points, Color Color, double Width);

/// <summary>
/// 折れ線データから OneNote に書き戻せる ISF (Ink Serialized Format) を組み立てる。
/// 座標系は「キャプチャ画像のピクセル座標」で受け取り、ページ座標 (pt) に変換する。
/// </summary>
public static class InkBuilder
{
    /// <summary>ストローク群を ISF バイナリにする。空なら null。</summary>
    public static byte[]? BuildIsf(IEnumerable<InkStroke> strokes)
    {
        var collection = new StrokeCollection();
        foreach (var s in strokes)
        {
            if (s.Points.Length < 2) continue;
            var points = new StylusPointCollection();
            foreach (var p in s.Points)
                points.Add(new StylusPoint(p.X, p.Y));

            var stroke = new Stroke(points)
            {
                DrawingAttributes =
                {
                    Color = s.Color,
                    Width = s.Width,
                    Height = s.Width,
                    FitToCurve = false,
                },
            };
            collection.Add(stroke);
        }
        if (collection.Count == 0) return null;

        using var ms = new MemoryStream();
        collection.Save(ms);
        return ms.ToArray();
    }

    /// <summary>ISF の外接矩形 (ISF 内部の座標単位)。位置・サイズ指定に使う。</summary>
    public static Rect GetBounds(byte[] isf)
    {
        using var ms = new MemoryStream(isf);
        return new StrokeCollection(ms).GetBounds();
    }

    /// <summary>
    /// "10,20 30,40 50,60" 形式の座標列を解析する。カンマ区切りの点をスペースで並べる。
    /// 解析できない点は無視する。
    /// </summary>
    public static Point[] ParsePoints(string text)
    {
        var result = new List<Point>();
        foreach (var token in text.Split([' ', '\t', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split(',');
            if (parts.Length != 2) continue;
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                result.Add(new Point(x, y));
            }
        }
        return [.. result];
    }

    /// <summary>"#RRGGBB" または色名を Color に。解析できなければ赤。</summary>
    public static Color ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Colors.Red;
        try
        {
            var converted = System.Windows.Media.ColorConverter.ConvertFromString(text.Trim());
            if (converted is Color c) return c;
        }
        catch
        {
            // 未知の色名はフォールバック
        }
        return Colors.Red;
    }
}
