using System.Text.RegularExpressions;
using System.Windows.Media;
using Colors = System.Windows.Media.Colors;

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
                    var stroke = ParseInkBody(body);
                    if (stroke != null)
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

    private static InkStroke? ParseInkBody(string body)
    {
        var segments = body.Split('|', StringSplitOptions.TrimEntries);
        if (segments.Length == 0) return null;

        var points = InkBuilder.ParsePoints(segments[0]);
        if (points.Length < 2) return null;

        var color = Colors.Red;
        var width = 2.0;
        foreach (var seg in segments.Skip(1))
        {
            var kv = seg.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;
            if (kv[0].Equals("color", StringComparison.OrdinalIgnoreCase))
                color = InkBuilder.ParseColor(kv[1]);
            else if (kv[0].Equals("width", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(kv[1], out var w) && w > 0)
                width = w;
        }
        return new InkStroke(points, color, width);
    }

    /// <summary>ディレクティブを取り除いた、通知バルーン用のプレーンテキスト。</summary>
    public static string StripDirectives(string response) =>
        Directive.Replace(response, "").Trim();
}
