using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace ClaudeNote;

public sealed class TrayContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly NotifyIcon _icon;
    private readonly HotkeyWindow _hotkey;
    private readonly AskFlow _flow;
    private readonly FloatButtonForm? _floatButton;
    private readonly ForegroundWatcher? _foreground;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private DateTime _lastProgressBalloon;

    public TrayContext(AppConfig config)
    {
        _config = config;
        _flow = new AskFlow(config);

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = $"ClaudeNote ({config.Hotkey})",
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("設定ファイルを開く", null, (_, _) => OpenConfig());
        menu.Items.Add("キャプチャフォルダを開く", null, (_, _) => OpenFolder(Logger.BaseDir));
        menu.Items.Add("ログを開く", null, (_, _) => OpenFile(Logger.LogPath));
        menu.Items.Add("会話セッションをリセット", null, (_, _) =>
        {
            SessionStore.ResetAll();
            _icon.ShowBalloonTip(2000, "ClaudeNote", "会話セッションの対応をリセットしました。次回は新規会話から始まります。", ToolTipIcon.Info);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitApp());
        _icon.ContextMenuStrip = menu;

        _hotkey = new HotkeyWindow();
        try
        {
            _hotkey.Register(config.Hotkey);
        }
        catch (UserFacingException ex)
        {
            _icon.ShowBalloonTip(5000, "ClaudeNote", ex.Message, ToolTipIcon.Error);
            Logger.Log(ex.Message);
        }
        _hotkey.HotkeyPressed += OnHotkey;

        if (config.FloatButton)
        {
            _floatButton = new FloatButtonForm(Math.Max(config.FloatButtonSize, 32), () => OnHotkey());

            // ボタンは OneNote が前面のときだけ出す。ボタン自身は WS_EX_NOACTIVATE で
            // 前面にならないため、タップしても OneNote が前面のまま保たれる
            _foreground = new ForegroundWatcher("ONENOTE");
            _foreground.ForegroundChanged += isOneNote => ApplyButtonVisibility(isOneNote);
            ApplyButtonVisibility(_foreground.IsTargetForeground);
        }

        Logger.Log($"起動しました。ホットキー: {config.Hotkey}");
        _icon.ShowBalloonTip(2000, "ClaudeNote",
            $"常駐を開始しました。OneNote で範囲を選択して {config.Hotkey} を押してください。", ToolTipIcon.Info);
    }

    /// <summary>OneNote が前面のときだけボタンを見せる。フォーカスを奪わないよう ShowWithoutActivation に任せる。</summary>
    private void ApplyButtonVisibility(bool isOneNoteForeground)
    {
        if (_floatButton == null || _floatButton.IsDisposed) return;
        if (isOneNoteForeground)
        {
            if (!_floatButton.Visible) _floatButton.Show();
        }
        else if (_floatButton.Visible)
        {
            _floatButton.Hide();
        }
    }

    private async void OnHotkey()
    {
        Logger.Log("ホットキー受信");
        if (_busy)
        {
            // 処理中の再操作はキャンセル
            if (_cts is { IsCancellationRequested: false } cts)
            {
                Logger.Log("キャンセル要求");
                _icon.ShowBalloonTip(2000, "ClaudeNote", "キャンセルしています…", ToolTipIcon.Info);
                cts.Cancel();
            }
            return;
        }

        _busy = true;
        _floatButton?.SetBusy(true);
        _cts = new CancellationTokenSource();
        var prevText = _icon.Text;
        _icon.Text = "ClaudeNote - Claude に問い合わせ中…";
        _icon.ShowBalloonTip(2000, "ClaudeNote",
            "受け付けました。選択内容をキャプチャして Claude に送ります…", ToolTipIcon.Info);
        _lastProgressBalloon = DateTime.Now;
        try
        {
            var result = await _flow.RunAsync(onProgress: detail =>
            {
                var text = $"ClaudeNote - {detail}";
                _icon.Text = text.Length <= 60 ? text : text[..60];
                // 長い実行でも生存が分かるよう、3分おきに現在の作業をバルーン通知する
                if ((DateTime.Now - _lastProgressBalloon).TotalSeconds >= 180)
                {
                    _lastProgressBalloon = DateTime.Now;
                    var d = detail.Length <= 100 ? detail : detail[..100];
                    _icon.ShowBalloonTip(1500, "ClaudeNote", $"実行中: {d}", ToolTipIcon.Info);
                }
            }, _cts.Token);
            var plain = ResponseParser.StripDirectives(result.Response);
            var preview = plain.Length > 80 ? plain[..80] + "…" : plain;
            var mode = result.Resumed ? "会話の続き" : "新規会話";
            _icon.ShowBalloonTip(3000, "ClaudeNote", $"ノートに挿入しました ({mode}):\n{preview}", ToolTipIcon.Info);
            Logger.Log($"挿入完了 ({mode}, {result.Response.Length}文字) artifacts={result.ArtifactsDir}");
        }
        catch (OperationCanceledException)
        {
            // キャンセル時はノートへ挿入せず、セッション ID も更新しない
            // (中断した会話が次回の resume 対象になると文脈が壊れるため)
            _icon.ShowBalloonTip(2500, "ClaudeNote", "キャンセルしました。ノートには何も挿入していません。", ToolTipIcon.Info);
            Logger.Log("キャンセルしました");
        }
        catch (UserFacingException ex)
        {
            _icon.ShowBalloonTip(4000, "ClaudeNote", ex.Message, ToolTipIcon.Warning);
            Logger.Log($"中断: {ex.Message}");
        }
        catch (Exception ex)
        {
            _icon.ShowBalloonTip(4000, "ClaudeNote", $"エラーが発生しました: {ex.Message}", ToolTipIcon.Error);
            Logger.Log(ex);
        }
        finally
        {
            _icon.Text = prevText;
            _busy = false;
            _floatButton?.SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Log(ex); }
    }

    private static void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, "");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Log(ex); }
    }

    /// <summary>実際に読み込まれている個人設定を開く (無ければテンプレートから復元)。</summary>
    private void OpenConfig()
    {
        try
        {
            var path = AppConfig.UserConfigPath;
            if (!File.Exists(path) && File.Exists(AppConfig.SampleConfigPath))
            {
                Directory.CreateDirectory(Logger.BaseDir);
                File.Copy(AppConfig.SampleConfigPath, path);
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            _icon.ShowBalloonTip(3000, "ClaudeNote",
                "編集後は「終了」して起動し直すと反映されます。", ToolTipIcon.Info);
        }
        catch (Exception ex) { Logger.Log(ex); }
    }

    private void ExitApp()
    {
        _cts?.Cancel();
        _hotkey.Dispose();
        _foreground?.Dispose();
        _floatButton?.Dispose();
        ClaudeSidecar.Shutdown();
        _icon.Visible = false;
        _icon.Dispose();
        ExitThread();
    }
}
