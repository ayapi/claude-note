using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeNote;

/// <summary>
/// WAV を文字起こしする。エンジンは設定で選べる:
///   whisper … whisper.cpp (高精度。モデルの配置が必要)
///   windows … System.Speech (Windows 標準。追加インストール不要だが精度は劣る)
///   auto    … whisper が使えれば whisper、無ければ windows
/// </summary>
public static class Transcriber
{
    public static async Task<string> TranscribeAsync(AppConfig config, string wavPath, CancellationToken ct = default)
    {
        var engine = config.SttEngine.ToLowerInvariant();
        if (engine == "auto")
            engine = ResolveWhisperExe(config) != null ? "whisper" : "windows";

        Logger.Log($"文字起こし開始 (engine={engine}): {wavPath}");
        var text = engine switch
        {
            "whisper" => await WhisperAsync(config, wavPath, ct),
            "windows" => await Task.Run(() => WindowsSpeech(config, wavPath), ct),
            _ => throw new UserFacingException($"未知の sttEngine です: {config.SttEngine}"),
        };

        text = Clean(text);
        Logger.Log($"文字起こし結果 ({text.Length}文字): {Truncate(text, 100)}");
        return text;
    }

    // ---- whisper.cpp ----

    private static async Task<string> WhisperAsync(AppConfig config, string wavPath, CancellationToken ct)
    {
        var exe = ResolveWhisperExe(config)
            ?? throw new UserFacingException(
                "whisper が見つかりません。appsettings.json の whisperExe / whisperModel を設定するか、sttEngine を \"windows\" にしてください。");
        var model = config.WhisperModel;
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(Environment.ExpandEnvironmentVariables(model)))
            throw new UserFacingException($"whisper のモデルが見つかりません: {model}");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[]
        {
            "-m", Environment.ExpandEnvironmentVariables(model),
            "-f", wavPath,
            "-l", config.SttLanguage,
            "--no-prints", "--no-timestamps",
        }) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new UserFacingException("whisper を起動できませんでした。");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stdout = await stdoutTask;
        if (proc.ExitCode != 0)
        {
            var stderr = await stderrTask;
            Logger.Log($"whisper exit {proc.ExitCode}: {Truncate(stderr, 300)}");
            throw new UserFacingException($"文字起こしに失敗しました (whisper exit {proc.ExitCode})。");
        }
        return stdout;
    }

    private static string? ResolveWhisperExe(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.WhisperExe))
        {
            var p = Environment.ExpandEnvironmentVariables(config.WhisperExe);
            return File.Exists(p) ? p : null;
        }
        return null;
    }

    // ---- Windows 標準 (System.Speech) ----

    private static string WindowsSpeech(AppConfig config, string wavPath)
    {
        // System.Speech は Windows 専用。プラットフォーム警告を避けるため呼び出しをここに閉じる
        if (!OperatingSystem.IsWindows())
            throw new UserFacingException("Windows 以外では windows エンジンを使えません。");

        var culture = new CultureInfo(config.SttLanguage.Equals("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "ja-JP");
        var recognizer = System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers()
            .FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == culture.TwoLetterISOLanguageName)
            ?? throw new UserFacingException(
                $"{culture.Name} の音声認識エンジンが見つかりません。Windows の設定で音声パックを追加するか、sttEngine を \"whisper\" にしてください。");

        using var engine = new System.Speech.Recognition.SpeechRecognitionEngine(recognizer);
        engine.LoadGrammar(new System.Speech.Recognition.DictationGrammar());
        engine.SetInputToWaveFile(wavPath);

        var sb = new StringBuilder();
        while (true)
        {
            System.Speech.Recognition.RecognitionResult? result;
            try
            {
                result = engine.Recognize();
            }
            catch (InvalidOperationException)
            {
                break; // 入力の終端
            }
            if (result == null) break;
            sb.Append(result.Text);
        }
        return sb.ToString();
    }

    // ---- 共通 ----

    private static readonly Regex BracketNoise = new(@"[\[\(（【](?:BLANK_AUDIO|音楽|拍手|無音)[^\]\)）】]*[\]\)）】]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>whisper が出す効果音表記や余分な空白を落とす。</summary>
    private static string Clean(string text)
    {
        text = BracketNoise.Replace(text, "");
        text = Regex.Replace(text, @"[ \t]+", " ");
        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
        return string.Join(" ", lines).Trim();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
