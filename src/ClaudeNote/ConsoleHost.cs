using System.IO;
using System.Runtime.InteropServices;

namespace ClaudeNote;

/// <summary>
/// GUI アプリ (WinExe) からコンソール出力を見えるようにする。
///
/// WinExe はコンソールに割り当てられないため、そのままでは Console.WriteLine が
/// どこにも出ない。PowerShell から診断コマンドを叩いても「何も起こらない」ように
/// 見えるのはこのため。呼び出し元のコンソールに相乗りするか、無ければ自前で開く。
///
/// Console.IsOutputRedirected では判断できない。WinExe は標準出力ハンドル自体を
/// 持たないことがあり、.NET はそれを「リダイレクトされている」と答えるため、
/// 本当にパイプへ繋がっている場合と区別がつかない。ハンドルの種類を直接見る。
/// </summary>
public static class ConsoleHost
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;

    private const int FileTypeUnknown = 0x0000;
    private const int FileTypeDisk = 0x0001;
    private const int FileTypeChar = 0x0002;
    private const int FileTypePipe = 0x0003;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetFileType(IntPtr hFile);

    /// <summary>自前のウィンドウを開いたか (終了時に閉じないよう待つかの判断に使う)。</summary>
    public static bool OwnsWindow { get; private set; }

    /// <summary>
    /// コンソールを用意する。ownWindow を true にすると、呼び出し元に相乗りせず
    /// 必ず自前のウィンドウを開く。対話するコマンド向け: 相乗りだと、シェルは
    /// GUI アプリの終了を待たずにプロンプトへ戻るため、入力を奪い合ってしまう。
    /// </summary>
    public static void Ensure(bool ownWindow = false)
    {
        if (GetConsoleWindow() != IntPtr.Zero)
        {
            Logger.Log("コンソール: すでに持っているので何もしません");
            return;
        }

        // 本物のパイプやファイルへ向いているときだけ触らない。
        // ハンドルが無い (FileTypeUnknown) のは「出力先が無い」であって、
        // 呼び出し側が受け取る先を用意しているわけではない
        var stdout = GetStdHandle(StdOutputHandle);
        var type = stdout == IntPtr.Zero || stdout == new IntPtr(-1) ? FileTypeUnknown : GetFileType(stdout);
        if (type is FileTypeDisk or FileTypePipe)
        {
            Logger.Log($"コンソール: 出力はリダイレクト済み (type={type}) なので触りません");
            return;
        }

        var attached = !ownWindow && AttachConsole(AttachParentProcess);
        if (!attached)
        {
            if (!AllocConsole())
            {
                Logger.Log($"コンソール: 確保できませんでした (ownWindow={ownWindow}, " +
                    $"error={Marshal.GetLastWin32Error()})");
                return;
            }
            OwnsWindow = true;
        }
        Rebind();
        Logger.Log($"コンソール: {(OwnsWindow ? "自前のウィンドウを開きました" : "呼び出し元に相乗りしました")} " +
            $"(stdout type={type})");
    }

    /// <summary>
    /// 起動時に無効なハンドルへ束ねられた Console の入出力を、今のコンソールへ繋ぎ直す。
    /// これをしないと、コンソールを得たあとでも書き込みが捨てられる。
    /// 標準ハンドルは当てにならないので、コンソール装置を直接開く。
    /// </summary>
    private static void Rebind()
    {
        try
        {
            var conOut = new StreamWriter(File.Open("CONOUT$", FileMode.Open, FileAccess.Write,
                FileShare.ReadWrite)) { AutoFlush = true };
            Console.SetOut(conOut);
            Console.SetError(conOut);
            Console.SetIn(new StreamReader(File.Open("CONIN$", FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite)));
        }
        catch (Exception ex)
        {
            Logger.Log($"コンソール: 入出力を繋ぎ直せませんでした: {ex.Message}");
        }
    }

    /// <summary>自前ウィンドウのときだけ、閉じる前に読ませる。</summary>
    public static void WaitBeforeClose()
    {
        if (!OwnsWindow) return;
        Console.WriteLine();
        Console.Write("Enter キーで閉じます...");
        try { Console.ReadLine(); } catch { }
    }
}
