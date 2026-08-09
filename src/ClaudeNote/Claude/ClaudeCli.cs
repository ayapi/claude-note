using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ClaudeNote;

public sealed record ClaudeResult(string Text, string? SessionId);

/// <summary>claude CLI (-p モード) 経由で Claude に問い合わせる。認証は Claude Code のログインを使い回す。</summary>
public static class ClaudeCli
{
    public static async Task<ClaudeResult> AskAsync(AppConfig config, string prompt, string workDir,
        string? resumeSessionId = null, string[]? addDirs = null, CancellationToken ct = default)
    {
        var exe = Resolve(config.ClaudePath);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--allowedTools");
        foreach (var tool in config.AllowedTools)
            psi.ArgumentList.Add(tool);
        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(resumeSessionId);
        }
        foreach (var dir in addDirs ?? [])
        {
            psi.ArgumentList.Add("--add-dir");
            psi.ArgumentList.Add(dir);
        }
        if (!string.IsNullOrWhiteSpace(config.Model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(config.Model);
        }

        Logger.Log($"claude CLI 起動: {exe} (resume={resumeSessionId ?? "なし"}, cwd={workDir})");
        using var proc = Process.Start(psi)
            ?? throw new UserFacingException("claude CLI を起動できませんでした。");

        await proc.StandardInput.WriteAsync(prompt);
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(config.TimeoutSeconds, 10)));
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new UserFacingException($"Claude の応答が {config.TimeoutSeconds} 秒以内に返りませんでした。");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0)
        {
            Logger.Log($"claude CLI exit {proc.ExitCode}: {stderr}");
            // resume 対象セッションが見つからない場合など。呼び出し側でフォールバックできるよう区別する
            if (!string.IsNullOrWhiteSpace(resumeSessionId))
                throw new SessionResumeException(resumeSessionId,
                    $"セッション {resumeSessionId} の継続に失敗しました: {Truncate(stderr, 300)}");
            throw new UserFacingException($"claude CLI がエラーを返しました (exit {proc.ExitCode}): {Truncate(stderr, 300)}");
        }

        var result = ParseResult(stdout);
        if (result.Text.Length == 0)
            throw new UserFacingException("Claude から空の応答が返りました。");
        return result;
    }

    private static ClaudeResult ParseResult(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var text = root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
            var sessionId = root.TryGetProperty("session_id", out var s) ? s.GetString() : null;
            if (root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True)
                throw new UserFacingException($"Claude がエラーを返しました: {Truncate(text, 300)}");
            return new ClaudeResult(text.Trim(), sessionId);
        }
        catch (JsonException)
        {
            // JSON でなければ素のテキストとして扱う (旧バージョン CLI 向けフォールバック)
            Logger.Log("claude CLI の出力が JSON ではありません。テキストとして処理します。");
            return new ClaudeResult(stdout.Trim(), null);
        }
    }

    public static string Resolve(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (File.Exists(overridePath)) return overridePath;
            throw new UserFacingException($"設定された claudePath が見つかりません: {overridePath}");
        }

        string[] names = ["claude.cmd", "claude.exe", "claude.bat"];
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var dir in dirs)
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }
        throw new UserFacingException("claude CLI が見つかりません。`npm i -g @anthropic-ai/claude-code` でインストールするか、appsettings.json の claudePath を設定してください。");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
