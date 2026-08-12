using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClaudeNote;

/// <summary>
/// OneNote に「選択範囲をコピー」させ、クリップボードからインクを受け取る。
///
/// 背景: GetPageContent で ISF を取ると、選択が何本でもページ全体を
/// シリアライズするため、手書きの多いページでは 100 秒を超える。
/// コピー経由なら選択したぶんだけで済み、実測で 108 秒 → 3 秒になった。
/// </summary>
public static class ClipboardInk
{
    private const string IsfFormat = "Ink Serialized Format";

    /// <summary>
    /// 選択範囲をコピーして ISF を取り出す。取れなければ null (呼び出し側は COM 経由へ退避)。
    /// クリップボードを使うため UI (STA) スレッドから呼ぶこと。
    /// </summary>
    public static byte[]? TryCopySelection(int timeoutMs = 30000)
    {
        try
        {
            if (!FocusOneNote())
            {
                Logger.Log("コピー経由を断念: OneNote を前面にできませんでした");
                return null;
            }

            try { Clipboard.Clear(); } catch { }
            SendCopy();

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(150);
                try
                {
                    var data = Clipboard.GetDataObject();
                    if (data == null || !data.GetDataPresent(IsfFormat, false)) continue;
                    if (data.GetData(IsfFormat, false) is not MemoryStream ms) continue;
                    var bytes = ms.ToArray();
                    if (bytes.Length == 0) continue;
                    Logger.Log($"コピー経由でインクを取得: {bytes.Length:N0} bytes ({sw.ElapsedMilliseconds} ms)");
                    return bytes;
                }
                catch (ExternalException)
                {
                    // 他プロセスがクリップボードを掴んでいる。少し待って再試行
                }
            }
            Logger.Log($"コピー経由を断念: {timeoutMs} ms 以内にインクが取れませんでした");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log($"コピー経由で例外、COM 経由に切り替えます: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// OneNote を前面にする。既に前面ならなにもしない。
    /// 選択やスクロールを変えないよう NavigateTo は使わず、ウィンドウの前面化だけ行う。
    /// </summary>
    private static bool FocusOneNote()
    {
        if (IsOneNoteForeground()) return true;

        using var proc = Process.GetProcessesByName("ONENOTE").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (proc == null) return false;

        for (var i = 0; i < 6 && !IsOneNoteForeground(); i++)
        {
            ForceForeground(proc.MainWindowHandle);
            Thread.Sleep(300);
        }
        return IsOneNoteForeground();
    }

    private static bool IsOneNoteForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return string.Equals(proc.ProcessName, "ONENOTE", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>前面ロックを回避してウィンドウを前面にする。</summary>
    private static void ForceForeground(IntPtr target)
    {
        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var me = GetCurrentThreadId();
        AttachThreadInput(me, fgThread, true);
        SetForegroundWindow(target);
        AttachThreadInput(me, fgThread, false);
    }

    private static void SendCopy()
    {
        const byte vkControl = 0x11;
        const byte vkC = 0x43;
        const uint keyUp = 0x0002;
        keybd_event(vkControl, 0, 0, IntPtr.Zero);
        keybd_event(vkC, 0, 0, IntPtr.Zero);
        Thread.Sleep(120);
        keybd_event(vkC, 0, keyUp, IntPtr.Zero);
        keybd_event(vkControl, 0, keyUp, IntPtr.Zero);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
}
