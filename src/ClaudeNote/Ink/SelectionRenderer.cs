using System.IO;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudeNote;

public sealed record RenderResult(string PngPath, int WidthPx, int HeightPx, int InkCount, int SkippedInk, int ImageCount);

/// <summary>
/// 選択された ink (ISF) と画像を、ページ座標 (pt) に基づいて合成し透明 PNG に描画する。
/// </summary>
public static class SelectionRenderer
{
    private const double DipPerPt = 96.0 / 72.0;
    private const double Zoom = 2.0;           // 2x スーパーサンプリング (約192dpi 相当)
    private const double MaxLongEdgePx = 2200; // Claude に送る画像の長辺上限
    private const double PadPx = 12;

    public static RenderResult? RenderToPng(Selection sel, string outPath)
    {
        var positioned = new List<(StrokeCollection Strokes, Rect Natural, Rect RectPt)>();
        var unpositioned = new List<(StrokeCollection Strokes, Rect Natural)>();
        var images = new List<(BitmapSource Bitmap, Rect RectPt)>();
        var skipped = 0;

        foreach (var ink in sel.Ink)
        {
            StrokeCollection strokes;
            try
            {
                strokes = new StrokeCollection(new MemoryStream(ink.Isf));
            }
            catch (Exception ex)
            {
                skipped++;
                Logger.Log($"ISF の読み込みに失敗 (スキップ): {ex.Message}");
                continue;
            }
            if (strokes.Count == 0) { skipped++; continue; }

            var natural = strokes.GetBounds();
            if (ink.PageRectPt is Rect rect) positioned.Add((strokes, natural, rect));
            else unpositioned.Add((strokes, natural));
        }

        foreach (var img in sel.Images)
        {
            if (img.PageRectPt is not Rect rect) continue;
            try
            {
                var decoder = BitmapDecoder.Create(new MemoryStream(img.Data),
                    BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                images.Add((decoder.Frames[0], rect));
            }
            catch (Exception ex)
            {
                Logger.Log($"画像のデコードに失敗 (スキップ): {ex.Message}");
            }
        }

        if (positioned.Count == 0 && unpositioned.Count == 0 && images.Count == 0)
            return null;

        // 外接矩形 (pt)
        Rect? bbox = null;
        foreach (var r in positioned.Select(p => p.RectPt).Concat(images.Select(i => i.RectPt)))
            bbox = bbox is Rect b ? Rect.Union(b, r) : r;

        // 位置情報の無い ink (アウトライン内の InkWord など) は下に積む
        if (unpositioned.Count > 0)
        {
            var yCursor = bbox?.Bottom + 6 ?? 0;
            var xBase = bbox?.X ?? 0;
            foreach (var (strokes, natural) in unpositioned)
            {
                var wPt = Math.Max(natural.Width / DipPerPt, 0.5);
                var hPt = Math.Max(natural.Height / DipPerPt, 0.5);
                var rect = new Rect(xBase, yCursor, wPt, hPt);
                positioned.Add((strokes, natural, rect));
                bbox = bbox is Rect b ? Rect.Union(b, rect) : rect;
                yCursor += hPt + 4;
            }
        }

        var bounds = bbox!.Value;
        var pxPerPt = DipPerPt * Zoom;
        var longEdgePt = Math.Max(bounds.Width, bounds.Height);
        if (longEdgePt * pxPerPt > MaxLongEdgePx)
            pxPerPt = MaxLongEdgePx / longEdgePt;

        var widthPx = (int)Math.Ceiling(bounds.Width * pxPerPt + PadPx * 2);
        var heightPx = (int)Math.Ceiling(bounds.Height * pxPerPt + PadPx * 2);
        widthPx = Math.Clamp(widthPx, 16, 4096);
        heightPx = Math.Clamp(heightPx, 16, 4096);

        Rect Map(Rect rPt) => new(
            (rPt.X - bounds.X) * pxPerPt + PadPx,
            (rPt.Y - bounds.Y) * pxPerPt + PadPx,
            rPt.Width * pxPerPt,
            rPt.Height * pxPerPt);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            foreach (var (bitmap, rectPt) in images)
                dc.DrawImage(bitmap, Map(rectPt));

            foreach (var (strokes, natural, rectPt) in positioned)
            {
                var target = Map(rectPt);
                var defaultScale = pxPerPt / DipPerPt; // DIP → px の等倍ズーム
                var sx = natural.Width > 0.05 ? target.Width / natural.Width : defaultScale;
                var sy = natural.Height > 0.05 ? target.Height / natural.Height : defaultScale;

                var m = Matrix.Identity;
                m.Translate(-natural.X, -natural.Y);
                m.Scale(sx, sy);
                m.Translate(target.X, target.Y);

                var clone = strokes.Clone();
                clone.Transform(m, applyToStylusTip: true);
                foreach (Stroke s in clone)
                    s.Draw(dc);
            }
        }

        var rtb = new RenderTargetBitmap(widthPx, heightPx, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using (var fs = File.Create(outPath))
            encoder.Save(fs);

        return new RenderResult(outPath, widthPx, heightPx, positioned.Count, skipped, images.Count);
    }
}
