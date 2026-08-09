using System.IO;

namespace ClaudeNote;

public static class Logger
{
    private static readonly object Gate = new();

    public static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeNote");

    public static string LogPath => Path.Combine(BaseDir, "claude-note.log");

    public static string CapturesDir => Path.Combine(BaseDir, "captures");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(BaseDir);
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // ログ失敗で本体を落とさない
        }
    }

    public static void Log(Exception ex) => Log(ex.ToString());
}
