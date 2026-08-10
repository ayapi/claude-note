using System.IO;

namespace ClaudeNote;

/// <summary>
/// 設定ファイルを保持し、変更されていれば読み直す。
/// プロンプトを編集しながら試せるよう、実行のたびに最新の内容を使う。
/// 読み直しに失敗した場合は直前に成功した設定をそのまま使い続ける
/// (書きかけの JSON で既定値に戻ってしまうのを防ぐ)。
/// </summary>
public sealed class ConfigStore
{
    /// <summary>変更しても再起動するまで反映されない項目。</summary>
    private static readonly (string Name, Func<AppConfig, object?> Get)[] StartupOnly =
    [
        ("hotkey", c => c.Hotkey),
        ("floatButton", c => c.FloatButton),
        ("floatButtonSize", c => c.FloatButtonSize),
        ("nodePath", c => c.NodePath),
        ("sidecarDir", c => c.SidecarDir),
    ];

    private DateTime _lastWrite;

    public AppConfig Current { get; private set; }

    public ConfigStore()
    {
        Current = AppConfig.LoadDefault();
        _lastWrite = GetStamp();
    }

    private static DateTime GetStamp()
    {
        try
        {
            var path = AppConfig.EffectiveConfigPath;
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// ファイルが変更されていれば読み直す。
    /// 再起動が必要な項目が変わっていた場合は、その旨のメッセージを返す。
    /// </summary>
    public string? RefreshIfChanged()
    {
        var stamp = GetStamp();
        if (stamp == _lastWrite) return null;
        _lastWrite = stamp;

        AppConfig fresh;
        try
        {
            fresh = AppConfig.LoadStrict(AppConfig.EffectiveConfigPath);
        }
        catch (Exception ex)
        {
            Logger.Log($"設定の再読み込みに失敗、直前の設定を使い続けます: {ex.Message}");
            return $"設定ファイルを読めませんでした ({ex.Message})。直前の設定で動作します。";
        }

        var changed = StartupOnly
            .Where(f => !Equals(f.Get(Current), f.Get(fresh)))
            .Select(f => f.Name)
            .ToArray();

        Current = fresh;
        Logger.Log($"設定を読み直しました: {AppConfig.EffectiveConfigPath}");
        return changed.Length > 0
            ? $"次の項目は再起動後に反映されます: {string.Join(", ", changed)}"
            : null;
    }
}
