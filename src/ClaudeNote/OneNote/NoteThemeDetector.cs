using Microsoft.Win32;

namespace ClaudeNote;

/// <summary>
/// ノートのページ背景が暗いか (OneNote のダークモード) を判定する。
///
/// ページ XML の PageSettings は color="automatic" としか教えてくれず、
/// Office の UI テーマ設定とも一致しないため、次の 2 つで判断する:
///   Windows のアプリテーマが暗い  かつ  OneNote が「キャンバスを白のままにする」を使っていない
///
/// なぜ必要か: OneNote のダークモードは<b>インクは自動で反転する</b>が
/// <b>画像は反転しない</b>。そのため白背景の PNG を貼ると、暗いノートの上で
/// そこだけ白い板のように浮いてしまう。Claude に背景の色を伝えて、
/// 透明背景と読める色で図を作らせるために使う。
/// </summary>
public static class NoteThemeDetector
{
    public static bool IsDarkCanvas()
    {
        try
        {
            // 未設定なら Windows の既定は明るいテーマ
            if (ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme") is null or 1)
                return false;

            // OneNote 側で「キャンバスを白のままにする」が有効ならページは白いまま
            return ReadDword(@"Software\Microsoft\Office\16.0\OneNote\General",
                "DarkModeCanvasLightsOn") != 1;
        }
        catch (Exception ex)
        {
            Logger.Log($"ノートの背景色を判定できませんでした (白として扱います): {ex.Message}");
            return false;
        }
    }

    private static int? ReadDword(string subKey, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        return key?.GetValue(name) as int?;
    }
}
