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
    private static readonly Color CancelColor = ColorTranslator.FromHtml("#C0392B");
    private static readonly Color RecordColor = ColorTranslator.FromHtml("#D93025");
    private static readonly Color RecordBack = ColorTranslator.FromHtml("#FDECEA");

    private readonly Action _onTap;
    private readonly System.Windows.Forms.Timer _spinTimer;
    private readonly System.Windows.Forms.Timer? _longPressTimer;
    private readonly Image? _customImage;
    private readonly int _logicalSize;
    private bool _busy;
    private bool _hover;
    private bool _recording;
    private bool _longPressFired;
    private float _angle;
    private float _pulse;

    /// <summary>長押しの開始 (録音開始)。設定で音声入力が有効なときだけ呼ばれる。</summary>
    public event Action? LongPressStarted;

    /// <summary>長押しの終了 (録音停止)。LongPressStarted の後に必ず呼ばれる。</summary>
    public event Action? LongPressEnded;

    public FloatButtonForm(int size, Action onTap, int longPressMs = 0)
    {
        _onTap = onTap;
        _logicalSize = size;
        if (longPressMs > 0)
        {
            _longPressTimer = new System.Windows.Forms.Timer { Interval = longPressMs };
            _longPressTimer.Tick += (_, _) =>
            {
                _longPressTimer.Stop();
                _longPressFired = true;
                SetRecording(true);
                LongPressStarted?.Invoke();
            };
        }
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;

        _customImage = LoadCustomImage();

        _spinTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _spinTimer.Tick += (_, _) =>
        {
            _angle = (_angle + 18f) % 360f;
            _pulse = (_pulse + 0.15f) % 1f;
            Invalidate();
        };
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
        if (busy)
        {
            _spinTimer.Start();
        }
        else
        {
            _spinTimer.Stop();
            _angle = 0;
        }
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _longPressFired = false;
        // 処理中は長押しを受け付けない (タップ = キャンセルのみ)
        if (!_busy) _longPressTimer?.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        _longPressTimer?.Stop();

        if (_longPressFired)
        {
            _longPressFired = false;
            SetRecording(false);
            LongPressEnded?.Invoke();
            return;
        }
        // 短いタップ。処理中ならキャンセルを意味する。判断は呼び出し側 (TrayContext) が行う
        _onTap();
    }

    private void SetRecording(bool recording)
    {
        _recording = recording;
        if (recording) _spinTimer.Start();
        else if (!_busy) _spinTimer.Stop();
        _pulse = 0;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var size = ClientSize.Width;

        using (var back = new SolidBrush(_recording ? RecordBack : (_hover && !_busy ? HoverBack : Color.White)))
            g.FillEllipse(back, 0, 0, size - 1, size - 1);
        using (var ring = new Pen(_recording ? RecordColor : RingColor, _recording ? 2.5f : 1.5f))
            g.DrawEllipse(ring, 1, 1, size - 3, size - 3);

        var center = size / 2f;
        g.TranslateTransform(center, center);

        // 録音中はマイクを表す丸を明滅させる
        if (_recording)
        {
            var scale = 0.85f + 0.15f * (float)Math.Sin(_pulse * Math.PI * 2);
            var r = size * 0.16f * scale;
            using var brush = new SolidBrush(RecordColor);
            g.FillEllipse(brush, -r, -r, r * 2, r * 2);
            g.ResetTransform();
            return;
        }

        // 処理中にカーソルを乗せると×印になり、押すとキャンセルできることを示す
        if (_busy && _hover)
        {
            DrawCancel(g, size);
            g.ResetTransform();
            return;
        }

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

    /// <summary>キャンセルを表す×印を原点中心に描く。</summary>
    private static void DrawCancel(Graphics g, int size)
    {
        var arm = size * 0.20f;
        using var pen = new Pen(CancelColor, Math.Max(size * 0.07f, 2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, -arm, -arm, arm, arm);
        g.DrawLine(pen, -arm, arm, arm, -arm);
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
            _longPressTimer?.Dispose();
            _customImage?.Dispose();
        }
        base.Dispose(disposing);
    }
}
