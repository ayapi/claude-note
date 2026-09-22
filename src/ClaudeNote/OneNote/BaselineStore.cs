using System.IO;
using System.Text.Json;

namespace ClaudeNote;

/// <summary>
/// ページごとの「前回送ったときの姿」を保存する。
///
/// 次に送るとき、これと比べて増えたぶんだけを送る。Claude が書き込んだ応答や
/// 添削のインクも、送信後に取り直した基準に含めることで次の差分から外れる。
///
/// sessions.json とは分ける。あちらは手で編集して既存の会話へ繋ぐことを想定した
/// ファイルで、数百個の objectID を混ぜると読めなくなるため。
/// </summary>
public static class BaselineStore
{
    /// <summary>保持するページ数の上限。古いものから捨てる。</summary>
    private const int MaxPages = 50;

    public static string StorePath => Path.Combine(Logger.BaseDir, "baselines.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static PageBaseline? Load(string pageId)
    {
        var all = LoadAll();
        return all.TryGetValue(pageId, out var entry) ? entry : null;
    }

    /// <summary>基準を置き換える。書けなくても本題は止めない (次回が広めに出るだけ)。</summary>
    public static void Save(PageBaseline baseline)
    {
        if (string.IsNullOrEmpty(baseline.PageId)) return;
        try
        {
            var all = LoadAll();
            all[baseline.PageId] = baseline;

            // 古いページから捨てる
            if (all.Count > MaxPages)
            {
                foreach (var key in all.OrderByDescending(kv => kv.Value.TakenAt)
                             .Skip(MaxPages).Select(kv => kv.Key).ToList())
                {
                    all.Remove(key);
                }
            }

            Directory.CreateDirectory(Logger.BaseDir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(all, Options));
        }
        catch (Exception ex)
        {
            Logger.Log($"基準の保存に失敗しました (次回の差分が広くなります): {ex.Message}");
        }
    }

    /// <summary>そのページの基準を捨てる。次は全部が「新しく書かれたもの」になる。</summary>
    public static void Reset(string pageId)
    {
        try
        {
            var all = LoadAll();
            if (!all.Remove(pageId)) return;
            File.WriteAllText(StorePath, JsonSerializer.Serialize(all, Options));
            Logger.Log($"基準を消しました: {pageId}");
        }
        catch (Exception ex)
        {
            Logger.Log($"基準の削除に失敗: {ex.Message}");
        }
    }

    /// <summary>すべての基準を捨てる (会話のリセットと足並みを揃えるため)。</summary>
    public static void ResetAll()
    {
        try
        {
            if (File.Exists(StorePath)) File.Delete(StorePath);
            Logger.Log("すべての基準を消しました");
        }
        catch (Exception ex)
        {
            Logger.Log($"基準の全削除に失敗: {ex.Message}");
        }
    }

    private static Dictionary<string, PageBaseline> LoadAll()
    {
        try
        {
            if (!File.Exists(StorePath)) return new(StringComparer.Ordinal);
            var json = File.ReadAllText(StorePath);
            return JsonSerializer.Deserialize<Dictionary<string, PageBaseline>>(json, Options)
                ?? new(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // 壊れていたら作り直す。基準が無い = 全部を新規として送る、に落ちるだけ
            Logger.Log($"基準を読めませんでした、作り直します: {ex.Message}");
            return new(StringComparer.Ordinal);
        }
    }
}
