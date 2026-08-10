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
    private readonly System.Windows.Forms.Timer _longPressTimer;
    private readonly ToolTip _tip = new() { InitialDelay = 300, ReshowDelay = 100 };
    private bool _longPressEnabled;
    private readonly Image? _customImage;
    private readonly int _logicalSize;
    private bool _busy;
    private bool _hover;
    private bool _recording;
    private bool _pressed;
    private bool _longPressFired;
    private DateTime _lastPointerAt = DateTime.MinValue;
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
        _longPressTimer = new System.Windows.Forms.Timer();
        _longPressTimer.Tick += (_, _) =>
        {
            _longPressTimer.Stop();
            _longPressFired = true;
            SetRecording(true);
            LongPressStarted?.Invoke();
        };
        SetLongPress(longPressMs);
        UpdateTip();
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

    // ペン/タッチの「長押し = 右クリック」ジェスチャを無効化する。
    private const int WmTabletQuerySystemGestureStatus = 0x02CC;
    private const int TabletDisablePressAndHold = 0x00000001;
    private const int TabletDisablePenTapFeedback = 0x00000008;
    private const int TabletDisablePenBarrelFeedback = 0x00000010;
    private const int TabletDisableFlicks = 0x00010000;

    // ペン/タッチのマウス互換メッセージは「指を離した時に押下と解放がまとめて」届くため、
    // それでは長押しを判定できない。ポインタメッセージを直接受けて実際の押下/解放の
    // タイミングを取る。処理したポインタメッセージは既定処理に渡さず、
    // マウス互換メッセージへの変換を止める (二重処理の防止)。
    private const int WmPointerDown = 0x0246;
    private const int WmPointerUp = 0x0247;
    private const int WmPointerCaptureChanged = 0x024C;

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WmTabletQuerySystemGestureStatus:
                m.Result = TabletDisablePressAndHold | TabletDisablePenTapFeedback
                    | TabletDisablePenBarrelFeedback | TabletDisableFlicks;
                return;

            case WmPointerDown:
                _lastPointerAt = DateTime.Now;
                BeginPress("ペン/タッチ (ポインタ)");
                m.Result = IntPtr.Zero;
                return;

            case WmPointerUp:
                _lastPointerAt = DateTime.Now;
                EndPress();
                m.Result = IntPtr.Zero;
                return;

            case WmPointerCaptureChanged:
                // 途中でキャプチャを奪われた場合に録音が止まらなくなるのを防ぐ
                if (_pressed)
                {
                    Logger.Log("ボタン: ポインタのキャプチャが外れました");
                    EndPress();
                }
                break;
        }
        base.WndProc(ref m);
    }

    /// <summary>押下の開始。ポインタとマウスのどちらから来ても 1 回だけ処理する。</summary>
    private void BeginPress(string source)
    {
        if (_pressed) return;
        _pressed = true;
        _longPressFired = false;
        Logger.Log($"ボタン押下: 入力={source} 長押し={(_longPressEnabled ? "有効" : "無効")} busy={_busy}");
        // 処理中は長押しを受け付けない (タップ = キャンセルのみ)
        if (!_busy && _longPressEnabled) _longPressTimer.Start();
    }

    /// <summary>押下の終了。長押し中なら録音終了、そうでなければタップ。</summary>
    private void EndPress()
    {
        if (!_pressed) return;
        _pressed = false;
        _longPressTimer.Stop();

        if (_longPressFired)
        {
            _longPressFired = false;
            SetRecording(false);
            Logger.Log("ボタン解放: 長押し終了 (録音停止)");
            LongPressEnded?.Invoke();
            return;
        }
        Logger.Log("ボタン解放: タップ");
        _onTap();
    }

    /// <summary>直近の入力がペン/タッチ由来かを判定する (診断ログ用)。</summary>
    private static string InputSource()
    {
        // MI_WP_SIGNATURE: ペン・タッチ由来のマウスメッセージに付く署名
        const uint signature = 0xFF515700;
        const uint mask = 0xFFFFFF00;
        var extra = (uint)GetMessageExtraInfo().ToInt64();
        return (extra & mask) == signature ? "ペン/タッチ" : "マウス";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetMessageExtraInfo();

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolWindow;
            return cp;
        }
    }

    /// <summary>録音中かどうか (表示制御の判断に使う)。</summary>
    public bool IsRecording => _recording;

    /// <summary>長押しの判定時間を変更する。0 以下なら音声入力を無効にする (設定の再読み込み用)。</summary>
    public void SetLongPress(int longPressMs)
    {
        _longPressEnabled = longPressMs > 0;
        if (_longPressEnabled) _longPressTimer.Interval = Math.Max(longPressMs, 150);
        else _longPressTimer.Stop();
        UpdateTip();
    }

    /// <summary>いまの状態と、押すと何が起きるかを説明する。</summary>
    private void UpdateTip()
    {
        var text = _recording
            ? "録音中… 指を離すと文字起こしして送ります"
            : _busy
                ? "Claude が考えています。押すと中断します"
                : _longPressEnabled
                    ? "タップ: 選択範囲を送る / 長押し: 音声で質問"
                    : "タップ: 選択範囲を送る";
        _tip.SetToolTip(this, text);
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
        UpdateTip();
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || IsPointerEcho()) return;
        BeginPress(InputSource());
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || IsPointerEcho()) return;
        EndPress();
    }

    /// <summary>直前にポインタで処理済みなら、遅れて届くマウス互換メッセージは無視する。</summary>
    private bool IsPointerEcho() => (DateTime.Now - _lastPointerAt).TotalMilliseconds < 800;

    private void SetRecording(bool recording)
    {
        _recording = recording;
        if (recording) _spinTimer.Start();
        else if (!_busy) _spinTimer.Stop();
        _pulse = 0;
        UpdateTip();
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
            _longPressTimer.Dispose();
            _tip.Dispose();
            _customImage?.Dispose();
        }
        base.Dispose(disposing);
    }
}
