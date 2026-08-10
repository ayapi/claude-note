using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace ClaudeNote;

/// <summary>
/// 画面右下に常駐する丸いフローティングボタン。タップでホットキーと同じ動作をする。
/// WS_EX_NOACTIVATE でフォーカスを奪わないため、OneNote の選択状態を保ったまま
/// ペンや指でタップできる。
/// %LOCALAPPDATA%\ClaudeNote\button.png (または exe 隣の button.png) があれば
/// それをアイコンとして使い、無ければスパーク型を描画する。
/// </summary>
public sealed class FloatButtonForm : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private static readonly Color SparkColor = ColorTranslator.FromHtml("#D97757");
    private static readonly Color SparkBusyColor = ColorTranslator.FromHtml("#B8AC9F");
    private static readonly Color RingColor = ColorTranslator.FromHtml("#E0D8CE");
    private static readonly Color HoverBack = ColorTranslator.FromHtml("#FBF1EA");

    private readonly Action _onTap;
    private readonly System.Windows.Forms.Timer _spinTimer;
    private readonly Image? _customImage;
    private readonly int _logicalSize;
    private bool _busy;
    private bool _hover;
    private float _angle;

    public FloatButtonForm(int size, Action onTap)
    {
        _onTap = onTap;
        _logicalSize = size;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;

        _customImage = LoadCustomImage();

        _spinTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _spinTimer.Tick += (_, _) => { _angle = (_angle + 18f) % 360f; Invalidate(); };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyLayout();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyLayout();
    }

    /// <summary>モニタの DPI に合わせて実サイズ・位置・円形リージョンを再計算する。</summary>
    private void ApplyLayout()
    {
        var scale = DeviceDpi / 96f;
        var px = (int)Math.Round(_logicalSize * scale);
        var margin = (int)Math.Round(24 * scale);

        Size = new Size(px, px);
        var wa = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        Location = new Point(wa.Right - px - margin, wa.Bottom - px - margin);

        using var path = new GraphicsPath();
        path.AddEllipse(0, 0, px, px);
        Region = new Region(path);
        Invalidate();
    }

    /// <summary>表示してもフォーカスを取らない。</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolWindow;
            return cp;
        }
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        if (busy) _spinTimer.Start();
        else { _spinTimer.Stop(); _angle = 0; }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && !_busy) _onTap();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var size = ClientSize.Width;

        using (var back = new SolidBrush(_hover && !_busy ? HoverBack : Color.White))
            g.FillEllipse(back, 0, 0, size - 1, size - 1);
        using (var ring = new Pen(RingColor, 1.5f))
            g.DrawEllipse(ring, 1, 1, size - 3, size - 3);

        var center = size / 2f;
        g.TranslateTransform(center, center);
        if (_busy) g.RotateTransform(_angle);

        if (_customImage != null)
        {
            var imgSize = size * 0.62f;
            g.DrawImage(_customImage, -imgSize / 2f, -imgSize / 2f, imgSize, imgSize);
        }
        else
        {
            DrawSpark(g, size);
        }
        g.ResetTransform();
    }

    /// <summary>8方向の尖ったレイからなるスパークを原点中心に描く。</summary>
    private void DrawSpark(Graphics g, int size)
    {
        var tip = size * 0.30f;      // レイ先端までの半径
        var baseR = size * 0.07f;    // レイ根元の半径
        var halfW = size * 0.055f;   // レイ根元の半幅
        using var brush = new SolidBrush(_busy ? SparkBusyColor : SparkColor);
        for (var i = 0; i < 8; i++)
        {
            var state = g.Save();
            g.RotateTransform(i * 45f);
            g.FillPolygon(brush,
            [
                new PointF(baseR, -halfW),
                new PointF(tip, 0),
                new PointF(baseR, halfW),
            ]);
            g.Restore(state);
        }
    }

    private static Image? LoadCustomImage()
    {
        foreach (var dir in new[] { Logger.BaseDir, AppContext.BaseDirectory })
        {
            var path = Path.Combine(dir, "button.png");
            if (!File.Exists(path)) continue;
            try
            {
                // FromFile はファイルをロックするため、バイト列経由で読み込む
                using var ms = new MemoryStream(File.ReadAllBytes(path));
                return Image.FromStream(ms);
            }
            catch (Exception ex)
            {
                Logger.Log($"button.png の読み込みに失敗: {ex.Message}");
            }
        }
        return null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _spinTimer.Dispose();
            _customImage?.Dispose();
        }
        base.Dispose(disposing);
    }
}
