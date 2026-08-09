using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClaudeNote;

/// <summary>グローバルホットキーを受け取るメッセージ専用ウィンドウ。</summary>
public sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 1;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private bool _registered;

    public event Action? HotkeyPressed;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams());
    }

    public void Register(string spec)
    {
        var (mods, vk) = Parse(spec);
        if (!RegisterHotKey(Handle, HotkeyId, mods | ModNoRepeat, vk))
            throw new UserFacingException($"ホットキー {spec} を登録できませんでした。他のアプリと競合している可能性があります。appsettings.json の hotkey を変更してください。");
        _registered = true;
    }

    public static (uint Mods, uint Vk) Parse(string spec)
    {
        uint mods = 0;
        var key = Keys.None;
        foreach (var raw in spec.Split('+'))
        {
            var token = raw.Trim();
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModControl; break;
                case "alt": mods |= ModAlt; break;
                case "shift": mods |= ModShift; break;
                case "win": mods |= ModWin; break;
                default:
                    var name = token.Length == 1 && char.IsDigit(token[0]) ? "D" + token : token;
                    if (!Enum.TryParse(name, ignoreCase: true, out key) || key == Keys.None)
                        throw new UserFacingException($"ホットキー指定 '{spec}' を解釈できませんでした。");
                    break;
            }
        }
        if (key == Keys.None)
            throw new UserFacingException($"ホットキー指定 '{spec}' にキーがありません。例: Ctrl+Alt+A");
        return (mods, (uint)key);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
            HotkeyPressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_registered)
        {
            UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
