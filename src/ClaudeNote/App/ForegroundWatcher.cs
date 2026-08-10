using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeNote;

/// <summary>
/// 前面ウィンドウが目的のアプリかどうかを監視する。
/// ポーリングではなく EVENT_SYSTEM_FOREGROUND のフックで変化を拾う。
/// フックはメッセージループのあるスレッドから生成すること (UI スレッド)。
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;
    private const uint WineventSkipownprocess = 0x0002;

    private readonly string[] _processNames;
    private readonly WinEventProc _callback;  // GC されないよう保持する
    private IntPtr _hook;

    /// <summary>対象アプリが前面になった / 前面でなくなったときに呼ばれる。</summary>
    public event Action<bool>? ForegroundChanged;

    public bool IsTargetForeground { get; private set; }

    public ForegroundWatcher(params string[] processNames)
    {
        _processNames = processNames;
        _callback = OnWinEvent;
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero,
            _callback, 0, 0, WineventOutofcontext | WineventSkipownprocess);
        if (_hook == IntPtr.Zero)
            Logger.Log("前面ウィンドウのフックを設定できませんでした。ボタンは常時表示になります。");
        Refresh();
    }

    /// <summary>いま前面のウィンドウを見て状態を更新する。</summary>
    public void Refresh() => Update(GetForegroundWindow());

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint threadId, uint time)
    {
        // ウィンドウ自身のイベントだけを見る (OBJID_WINDOW = 0)
        if (objectId != 0) return;
        Update(hwnd);
    }

    /// <summary>直近に前面だったプロセス名 (診断ログ用)。</summary>
    public string LastForegroundProcess { get; private set; } = "";

    private void Update(IntPtr hwnd)
    {
        var name = ProcessNameOf(hwnd);
        var isTarget = _processNames.Any(n => string.Equals(name, n, StringComparison.OrdinalIgnoreCase));
        if (name != LastForegroundProcess)
        {
            LastForegroundProcess = name;
            Logger.Log($"前面が変わりました: {(name.Length > 0 ? name : "(不明)")} → 対象={isTarget}");
        }
        if (isTarget == IsTargetForeground) return;
        IsTargetForeground = isTarget;
        try
        {
            ForegroundChanged?.Invoke(isTarget);
        }
        catch (Exception ex)
        {
            Logger.Log($"前面変化の通知でエラー: {ex.Message}");
        }
    }

    private static string ProcessNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return "";
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            // 保護されたプロセスや終了直後のプロセスは判定できない
            return "";
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint threadId, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
