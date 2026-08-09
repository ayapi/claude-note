using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeNote;

public sealed class SessionEntry
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    /// <summary>claude CLI を実行する作業ディレクトリ。既存の Claude Code セッションに
    /// 接続する場合、そのセッションが属するプロジェクトのパスを指定する。</summary>
    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    /// <summary>人間が見て分かるためのメモ (セクション名など)。動作には影響しない。</summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// OneNote のセクション/ページ ID → claude セッション ID の対応を永続化する。
/// -p --resume は毎回新しいセッション ID にフォークするため、実行のたびに最新 ID へ更新する。
/// %LOCALAPPDATA%\ClaudeNote\sessions.json は手編集も想定 (既存セッションへの接続)。
/// </summary>
public sealed class SessionStore
{
    public static string StorePath => Path.Combine(Logger.BaseDir, "sessions.json");

    private readonly Dictionary<string, SessionEntry> _map;

    public SessionStore()
    {
        _map = Load();
    }

    private static Dictionary<string, SessionEntry> Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return new(StringComparer.Ordinal);
            var json = File.ReadAllText(StorePath);
            return JsonSerializer.Deserialize<Dictionary<string, SessionEntry>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? new(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Logger.Log($"sessions.json の読み込みに失敗、無視します: {ex.Message}");
            return new(StringComparer.Ordinal);
        }
    }

    public SessionEntry? Get(string key) => _map.TryGetValue(key, out var e) ? e : null;

    public void Update(string key, string sessionId, string? label = null)
    {
        if (_map.TryGetValue(key, out var e))
        {
            e.SessionId = sessionId;
            e.UpdatedAt = DateTime.Now;
            if (label != null) e.Label = label;
        }
        else
        {
            _map[key] = new SessionEntry { SessionId = sessionId, Label = label, UpdatedAt = DateTime.Now };
        }
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Logger.BaseDir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_map, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        catch (Exception ex)
        {
            Logger.Log($"sessions.json の保存に失敗: {ex.Message}");
        }
    }

    public static void ResetAll()
    {
        try { File.Delete(StorePath); } catch { }
    }
}
