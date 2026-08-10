using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ClaudeNote;

/// <summary>
/// Claude Agent SDK を動かす常駐 Node サイドカーとの通信。
/// プロトコルは stdin/stdout の JSON Lines (sidecar/index.mjs 参照)。
/// </summary>
public sealed class ClaudeSidecar : IDisposable
{
    private static readonly Lazy<ClaudeSidecar> Lazy = new(() => new ClaudeSidecar());
    public static ClaudeSidecar Instance => Lazy.Value;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _proc;
    private int _reqId;

    /// <summary>
    /// Claude に問い合わせる。タイムアウトは合計時間ではなく「無応答時間」で判定する:
    /// サイドカーから進行イベントが届くたびにタイマーをリセットするため、
    /// 大量の資料を読み歩く長いエージェント実行でも、動いている限り打ち切らない。
    /// </summary>
    public async Task<ClaudeResult> AskAsync(AppConfig config, string prompt, string cwd,
        string? resumeSessionId, string[] addDirs, Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var proc = EnsureStarted(config);
            var id = (++_reqId).ToString();

            var requestJson = JsonSerializer.Serialize(new
            {
                id,
                prompt,
                resume = resumeSessionId,
                cwd,
                addDirs,
                allowedTools = config.AllowedTools,
                model = config.Model,
            });

            Logger.Log($"sidecar 要求 #{id} (resume={resumeSessionId ?? "なし"}, cwd={cwd}, addDirs={addDirs.Length})");
            await WriteLineAsync(proc, requestJson, ct);

            var idle = TimeSpan.FromSeconds(Math.Max(config.TimeoutSeconds, 30));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(idle);
            try
            {
                while (true)
                {
                    var line = await proc.StandardOutput.ReadLineAsync(timeout.Token);
                    if (line == null)
                        throw new UserFacingException("サイドカーが終了しました。ログを確認してください。");

                    var parsed = ParseLine(line, id, resumeSessionId);
                    if (parsed.Kind == LineKind.Final)
                        return parsed.Result!;
                    if (parsed.Kind == LineKind.Progress)
                    {
                        timeout.CancelAfter(idle); // 進行がある限りタイマーを巻き直す
                        if (!string.IsNullOrWhiteSpace(parsed.Detail))
                        {
                            Logger.Log($"sidecar 進行: {parsed.Detail}");
                            onProgress?.Invoke(parsed.Detail!);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Restart();
                throw new UserFacingException(
                    $"Claude から {(int)idle.TotalSeconds} 秒間なにも進行イベントが届かなかったため、無応答とみなして中断しました。");
            }
            catch (OperationCanceledException)
            {
                // ユーザーによるキャンセル。ここで確実にサイドカーへ伝える。
                // (以前は CancellationToken.Register から送っていたが、登録の破棄と
                //  競合して送信されないことがあり、中断できなかった要求が走り続けて
                //  次の要求と同じセッションで衝突していた)
                await AbortOnSidecarAsync(proc, id);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// サイドカーに中断を伝え、その要求が終わったことを確認する。
    /// 一定時間内に確認できなければサイドカーを作り直す。走り続けている要求を残したまま
    /// 次の要求を投げると、同じセッションを取り合って応答が返らなくなるため。
    /// </summary>
    private async Task AbortOnSidecarAsync(Process proc, string id)
    {
        try
        {
            await WriteLineAsync(proc, JsonSerializer.Serialize(new { id, cancel = true }), CancellationToken.None);
            Logger.Log($"sidecar へキャンセルを送信 #{id}");
        }
        catch (Exception ex)
        {
            Logger.Log($"キャンセル送信に失敗、サイドカーを作り直します: {ex.Message}");
            Restart();
            return;
        }

        // 中断の完了 (= その id の最終応答) を待って読み捨てる
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                var line = await proc.StandardOutput.ReadLineAsync(wait.Token);
                if (line == null) break;
                if (IsFinalFor(line, id))
                {
                    Logger.Log($"sidecar のキャンセル完了を確認 #{id}");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Log($"サイドカーがキャンセルに応答しません。作り直します #{id}");
        }
        Restart();
    }

    /// <summary>指定 id に対する最終応答 (進行イベントではない) かどうか。</summary>
    private static bool IsFinalFor(string line, string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if ((root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null) != id) return false;
            return !root.TryGetProperty("event", out var ev) || ev.GetString() != "progress";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>サイドカーへの書き込みを直列化する (要求とキャンセルが別スレッドから来るため)。</summary>
    private async Task WriteLineAsync(Process proc, string json, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await proc.StandardInput.WriteLineAsync(json.AsMemory(), ct);
            await proc.StandardInput.FlushAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private enum LineKind { Skip, Progress, Final }

    private readonly record struct ParsedLine(LineKind Kind, ClaudeResult? Result, string? Detail);

    /// <summary>応答行を解釈する。id 不一致・非 JSON の行は読み飛ばす。</summary>
    private static ParsedLine ParseLine(string line, string id, string? resumeSessionId)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return new ParsedLine(LineKind.Skip, null, null); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new ParsedLine(LineKind.Skip, null, null);
            if ((root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null) != id)
                return new ParsedLine(LineKind.Skip, null, null);

            if (root.TryGetProperty("event", out var ev) && ev.GetString() == "progress")
            {
                var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
                return new ParsedLine(LineKind.Progress, null, detail);
            }

            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            var sessionId = root.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null;
            if (ok)
            {
                var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                if (text.Trim().Length == 0)
                    throw new UserFacingException("Claude から空の応答が返りました。");
                return new ParsedLine(LineKind.Final, new ClaudeResult(text.Trim(), sessionId), null);
            }

            var error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "不明なエラー" : "不明なエラー";
            if (root.TryGetProperty("canceled", out var cn) && cn.ValueKind == JsonValueKind.True)
                throw new OperationCanceledException(error);
            var resumeFailed = root.TryGetProperty("resumeFailed", out var rf) && rf.ValueKind == JsonValueKind.True;
            if (resumeFailed && resumeSessionId != null)
                throw new SessionResumeException(resumeSessionId, $"セッション継続に失敗: {error}");
            throw new UserFacingException($"Claude SDK エラー: {error}");
        }
    }

    private Process EnsureStarted(AppConfig config)
    {
        if (_proc is { HasExited: false }) return _proc;

        var script = ResolveScript(config.SidecarDir);
        var psi = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(config.NodePath) ? "node" : config.NodePath,
            WorkingDirectory = Path.GetDirectoryName(script)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(script);

        var proc = Process.Start(psi)
            ?? throw new UserFacingException("サイドカー (node) を起動できませんでした。Node.js がインストールされているか確認してください。");
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Logger.Log($"sidecar: {e.Data}"); };
        proc.BeginErrorReadLine();
        Logger.Log($"サイドカー起動: PID {proc.Id} ({script})");
        _proc = proc;
        return proc;
    }

    private static string ResolveScript(string? sidecarDir)
    {
        if (!string.IsNullOrWhiteSpace(sidecarDir))
        {
            var p = Path.Combine(sidecarDir, "index.mjs");
            if (File.Exists(p)) return p;
            throw new UserFacingException($"サイドカーが見つかりません: {p}");
        }

        // exe の場所から上に辿って sidecar/index.mjs を探す (bin\Release\... → リポジトリ直下)
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "sidecar", "index.mjs");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new UserFacingException("sidecar/index.mjs が見つかりません。appsettings.json の sidecarDir を設定してください。");
    }

    private void Restart()
    {
        try { _proc?.Kill(entireProcessTree: true); } catch { }
        _proc = null;
    }

    public static void Shutdown()
    {
        if (Lazy.IsValueCreated) Lazy.Value.Dispose();
    }

    public void Dispose()
    {
        Restart();
        _gate.Dispose();
    }
}
