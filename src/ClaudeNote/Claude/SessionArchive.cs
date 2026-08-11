using System.IO;

namespace ClaudeNote;

/// <summary>
/// Claude Code が残すセッション記録 (~/.claude/projects/&lt;project&gt;/&lt;id&gt;.jsonl) を探す。
/// resume に失敗したときに、その記録を新しいセッションへ読ませて文脈を引き継ぐために使う。
/// </summary>
public static class SessionArchive
{
    public static string ProjectsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>
    /// セッション ID から記録ファイルを探す。プロジェクト (作業ディレクトリ) が違っても
    /// 見つかるよう、projects 配下を横断して探す。見つからなければ null。
    /// </summary>
    public static string? Find(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        try
        {
            var root = ProjectsRoot;
            if (!Directory.Exists(root)) return null;
            var name = sessionId + ".jsonl";
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"セッション記録の探索に失敗: {ex.Message}");
        }
        return null;
    }

    /// <summary>ファイルサイズ (MB)。プロンプトで「大きいので全部読むな」と伝えるために使う。</summary>
    public static double SizeMb(string path)
    {
        try { return new FileInfo(path).Length / 1024.0 / 1024.0; }
        catch { return 0; }
    }
}
