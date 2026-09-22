using System.IO;

namespace ClaudeNote;

/// <summary>exe の置き場所を起点にした探索と、設定に書かれたパスの解決。</summary>
internal static class AppPaths
{
    /// <summary>
    /// exe の置き場所から上に向かってディレクトリを列挙する。
    ///
    /// AppContext.BaseDirectory は末尾に区切り文字が付き、Path.GetDirectoryName は
    /// 末尾区切りのパスに対して「区切りを落としただけの同じパス」を返す。起点で
    /// 区切りを落としておかないと最初の 1 回が空振りし、たどれる階層が 1 つ減る。
    /// 標準のビルド出力 (src\ClaudeNote\bin\Release\net9.0-windows) はリポジトリ直下から
    /// ちょうど 5 階層なので、この 1 つの差でリポジトリ直下に届かなくなる。
    /// </summary>
    public static IEnumerable<string> AncestorsFromExe(int levels = 10)
    {
        var dir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        for (var i = 0; i < levels && !string.IsNullOrEmpty(dir); i++)
        {
            yield return dir;
            dir = Path.GetDirectoryName(dir);
        }
    }

    /// <summary>
    /// 設定に書かれたパスを使える形にする。%USERPROFILE% などを展開し、前後の空白を落とす。
    /// 複数の PC で同じ設定ファイルを使い回せるようにするため、パスを受け取る設定項目は
    /// すべてここを通す。未設定なら null。
    /// </summary>
    public static string? Expand(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Environment.ExpandEnvironmentVariables(path.Trim());
}
