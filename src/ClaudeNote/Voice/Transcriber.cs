using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeNote;

/// <summary>
/// WAV を文字起こしする。エンジンは設定で選べる:
///   whisper … whisper.cpp (高精度。ローカルで動くが exe とモデルの配置が必要)
///   openai  … OpenAI の文字起こし API (高精度。キーと通信が必要で従量課金)
///   windows … System.Speech (Windows 標準。追加インストール不要だが精度は劣る)
///   auto    … 使えるものを whisper → openai → windows の順に選ぶ
///
/// auto のときは、選んだエンジンが失敗しても次の候補で試す。
/// whisper を置けない PC でも、キーがあれば実用的な精度で動かせるようにするため。
/// </summary>
public static class Transcriber
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static async Task<string> TranscribeAsync(AppConfig config, string wavPath, CancellationToken ct = default)
    {
        var requested = config.SttEngine.ToLowerInvariant();
        var chain = requested == "auto" ? AutoChain(config) : new[] { requested };

        // 先頭の無音を落とし音量を揃えてから渡す。ここで音声が見つからなければ
        // どのエンジンでも幻聴めいた結果しか返ってこないので送らない
        var cleaned = AudioCleaner.Clean(wavPath);
        if (cleaned.SpeechDuration.TotalSeconds < 0.3)
            throw new UserFacingException("音声が検出できませんでした。マイクに近づいて、はっきり話してください。");
        wavPath = cleaned.WavPath;

        for (var i = 0; i < chain.Length; i++)
        {
            var engine = chain[i];
            Logger.Log($"文字起こし開始 (engine={engine}): {wavPath}");
            try
            {
                var text = Clean(engine switch
                {
                    "whisper" => await WhisperAsync(config, wavPath, ct),
                    "openai" => await OpenAiAsync(config, wavPath, ct),
                    "windows" => await Task.Run(() => WindowsSpeech(config, wavPath), ct),
                    _ => throw new UserFacingException($"未知の sttEngine です: {config.SttEngine}"),
                });
                Logger.Log($"文字起こし結果 ({text.Length}文字): {Truncate(text, 100)}");
                return text;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && i < chain.Length - 1)
            {
                Logger.Log($"{engine} での文字起こしに失敗しました。{chain[i + 1]} で試し直します: {ex.Message}");
            }
        }
        throw new UserFacingException("文字起こしのエンジンがひとつも使えませんでした。");
    }

    /// <summary>auto のときに試す順番。使える見込みのあるものだけを並べる。</summary>
    private static string[] AutoChain(AppConfig config)
    {
        var chain = new List<string>();
        if (ResolveWhisperExe(config) != null && ResolveWhisperModel(config) != null) chain.Add("whisper");
        if (config.ResolveOpenAiApiKey() != null) chain.Add("openai");
        chain.Add("windows"); // 追加インストール不要なので最後の砦として必ず入れる
        return chain.ToArray();
    }

    // ---- whisper.cpp ----

    private static async Task<string> WhisperAsync(AppConfig config, string wavPath, CancellationToken ct)
    {
        var exe = ResolveWhisperExe(config)
            ?? throw new UserFacingException(
                "whisper が見つかりません。appsettings.json の whisperExe / whisperModel を設定するか、sttEngine を \"openai\" か \"windows\" にしてください。");
        var model = ResolveWhisperModel(config)
            ?? throw new UserFacingException($"whisper のモデルが見つかりません: {config.WhisperModel}");

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
            "-m", model,
            "-f", wavPath,
            "-l", config.SttLanguage,
            "--no-prints", "--no-timestamps",
        }) psi.ArgumentList.Add(a);
        var hint = ResolvePrompt(config);
        if (hint != null)
        {
            psi.ArgumentList.Add("--prompt");
            psi.ArgumentList.Add(hint);
        }

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

    private static string? ResolveWhisperExe(AppConfig config) => ResolveExisting(config.WhisperExe);

    private static string? ResolveWhisperModel(AppConfig config) => ResolveExisting(config.WhisperModel);

    private static string? ResolveExisting(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = Environment.ExpandEnvironmentVariables(path);
        return File.Exists(p) ? p : null;
    }

    // ---- OpenAI の文字起こし API ----

    private static async Task<string> OpenAiAsync(AppConfig config, string wavPath, CancellationToken ct)
    {
        var key = config.ResolveOpenAiApiKey()
            ?? throw new UserFacingException(
                "OpenAI のキーがありません。appsettings.json の openaiApiKey か、環境変数 OPENAI_API_KEY を設定してください。");

        using var form = new MultipartFormDataContent();
        await using var wav = File.OpenRead(wavPath);
        var file = new StreamContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", Path.GetFileName(wavPath));
        form.Add(new StringContent(config.OpenAiSttModel), "model");
        if (!string.IsNullOrWhiteSpace(config.SttLanguage))
            form.Add(new StringContent(config.SttLanguage), "language");
        // 言語と話題のヒント。短い発話だと language 指定だけでは中国語や英語に
        // 化けることがあるため、同じ言語の文を先に見せて引き戻す
        var hint = ResolvePrompt(config);
        if (hint != null)
            form.Add(new StringContent(hint, Encoding.UTF8), "prompt");
        form.Add(new StringContent("text"), "response_format");

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions")
        {
            Content = form,
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var res = await Http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            // 本文にキーは含まれないが、念のため長さを切って記録する
            Logger.Log($"OpenAI 文字起こし {(int)res.StatusCode}: {Truncate(body, 300)}");
            throw new UserFacingException(
                $"OpenAI の文字起こしに失敗しました ({(int)res.StatusCode})。モデル {config.OpenAiSttModel} が使えない場合は openaiSttModel を \"whisper-1\" にしてください。");
        }
        return body;
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

    /// <summary>
    /// 認識のヒント文。設定があればそれを、無ければ言語に合わせた既定文を使う。
    /// whisper の --prompt と OpenAI の prompt はどちらも「直前の文脈」として扱われるので、
    /// 認識してほしい言語・文体で書いた自然な文にする。
    /// </summary>
    private static string? ResolvePrompt(AppConfig config)
    {
        if (config.SttPrompt != null)
            return string.IsNullOrWhiteSpace(config.SttPrompt) ? null : config.SttPrompt;
        return config.SttLanguage.Equals("en", StringComparison.OrdinalIgnoreCase)
            ? "A student asks a question about their handwritten notes."
            : "生徒が手書きのノートについて質問しています。この問題の解き方を教えてください。";
    }

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
