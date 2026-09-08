using System.Diagnostics;
using System.IO;
using System.Text;

namespace ClaudeNote;

/// <summary>
/// GitHub から更新を取り込んでビルドし、アプリを入れ替える。
///
/// 実行中は ClaudeNote.exe がロックされていてビルドが上書きできないため、
/// 取得とビルドはアプリを終了させてから外部スクリプトにやらせる。アプリ側は
/// 「何が来るか」を調べて見せるところまでを担当する。
/// </summary>
public static class Updater
{
    /// <summary>更新の可否と内容。<see cref="Blocker"/> が非 null なら更新できない。</summary>
    public sealed record UpdateStatus(string? RepoRoot, int Behind, string[] Commits, string? Blocker)
    {
        public bool CanUpdate => Blocker == null && Behind > 0;
    }

    /// <summary>exe の位置から上に辿って git リポジトリの根を探す。</summary>
    public static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            if (Directory.Exists(Path.Combine(dir, ".git"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>リモートを見て、取り込める更新があるかを調べる。</summary>
    public static UpdateStatus Check()
    {
        var root = FindRepoRoot();
        if (root == null)
            return new UpdateStatus(null, 0, [], "ClaudeNote のリポジトリが見つかりません。git clone した場所からビルドした exe でないと更新できません。");

        if (Run(root, "--version").ExitCode != 0)
            return new UpdateStatus(root, 0, [], "git が見つかりません。git をインストールしてください。");

        var dirty = Run(root, "status", "--porcelain");
        if (dirty.ExitCode == 0 && dirty.Output.Trim().Length > 0)
            return new UpdateStatus(root, 0, [], $"ローカルに未コミットの変更があるため更新できません:\n{Truncate(dirty.Output.Trim(), 400)}");

        var fetch = Run(root, "fetch", "--quiet", "origin");
        if (fetch.ExitCode != 0)
            return new UpdateStatus(root, 0, [], $"リモートの取得に失敗しました:\n{Truncate(fetch.Error.Trim(), 400)}");

        // 追跡ブランチが未設定のリポジトリでも動くよう origin/main に寄せる
        var upstream = Run(root, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        var target = upstream.ExitCode == 0 && upstream.Output.Trim().Length > 0
            ? upstream.Output.Trim()
            : "origin/main";

        var count = Run(root, "rev-list", "--count", $"HEAD..{target}");
        if (count.ExitCode != 0 || !int.TryParse(count.Output.Trim(), out var behind))
            return new UpdateStatus(root, 0, [], $"更新の有無を判定できませんでした ({target})。");

        var log = Run(root, "log", "--oneline", "--no-decorate", $"HEAD..{target}");
        var commits = log.Output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return new UpdateStatus(root, behind, commits, null);
    }

    /// <summary>
    /// 更新スクリプトを起こしてアプリを終了する。あとは
    /// スクリプトが pull → npm install → build → 起動 まで面倒を見る。
    /// </summary>
    public static void ApplyAndRestart(string repoRoot)
    {
        var exe = Environment.ProcessPath
            ?? throw new UserFacingException("実行中の exe のパスを特定できませんでした。");
        var scriptPath = Path.Combine(Logger.BaseDir, "update.ps1");
        var logPath = Path.Combine(Logger.BaseDir, "update.log");

        // PowerShell 5.1 は BOM が無いと .ps1 を ANSI として読むため、日本語が化けないよう BOM 付きで書く
        File.WriteAllText(scriptPath, ScriptBody, new UTF8Encoding(true));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            UseShellExecute = true, // 進行が見えるようコンソールを出す
        };
        foreach (var a in new[]
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
            "-AppPid", Environment.ProcessId.ToString(),
            "-Repo", repoRoot,
            "-Exe", exe,
            "-LogPath", logPath,
        }) psi.ArgumentList.Add(a);

        Process.Start(psi);
        Logger.Log($"更新スクリプトを起動しました: {scriptPath} (log={logPath})");
    }

    private const string ScriptBody = """
        param(
          [int]$AppPid,
          [string]$Repo,
          [string]$Exe,
          [string]$LogPath
        )
        # ClaudeNote が自分自身を更新するためのスクリプト。アプリ側から起動される。
        # 実行中は exe がロックされてビルドできないので、まず終了を待つ。
        $ErrorActionPreference = "Continue"
        # 日本語版の dotnet / npm の出力をログで読めるようにする (既定のコードページだと化ける)
        try {
          [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
          $OutputEncoding = [System.Text.Encoding]::UTF8
          $env:DOTNET_CLI_UI_LANGUAGE = "en"
        } catch { }
        function Log($m) {
          $line = "[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $m
          Write-Host $line
          Add-Content -Path $LogPath -Value $line -Encoding UTF8
        }

        Log "更新を開始します (repo=$Repo)"
        Write-Host ""
        Write-Host "ClaudeNote を更新しています。このウィンドウは終わると自動で閉じます。"
        Write-Host ""

        Log "ClaudeNote の終了を待っています (PID $AppPid)"
        try { Wait-Process -Id $AppPid -Timeout 60 -ErrorAction Stop } catch { }
        if (Get-Process -Id $AppPid -ErrorAction SilentlyContinue) {
          Log "終了しなかったので停止させます"
          Stop-Process -Id $AppPid -Force -ErrorAction SilentlyContinue
          Start-Sleep -Seconds 2
        }

        # 途中で失敗したときに取り込みごと巻き戻すため、更新前の位置を控える。
        # ソースだけ進んでバイナリが古いまま残ると、次の確認で「最新です」と出て
        # 古い exe を使い続けることになるため、ソースとバイナリは必ず一緒に進める。
        $script:PrevSha = (& git -C $Repo rev-parse HEAD 2>$null | Out-String).Trim()
        Log "更新前の位置: $script:PrevSha"

        function Fail($m) {
          Log "失敗: $m"
          if ($script:PrevSha) {
            Log "取り込みを $script:PrevSha に巻き戻します"
            & git -C $Repo reset --hard $script:PrevSha 2>&1 | Out-Null
          }
          Write-Host ""
          Write-Host "更新に失敗しました: $m" -ForegroundColor Red
          Write-Host "更新前の状態に戻したので、ClaudeNote はこれまでどおり使えます。" -ForegroundColor Yellow
          Write-Host "ログ: $LogPath"
          Write-Host ""
          if (Test-Path $Exe) {
            Write-Host "更新前の ClaudeNote を起動します。"
            Start-Process -FilePath $Exe
          }
          Read-Host "Enter キーで閉じます"
          exit 1
        }

        Log "git pull --ff-only"
        $out = & git -C $Repo pull --ff-only 2>&1 | Out-String
        Log $out.Trim()
        if ($LASTEXITCODE -ne 0) { Fail "git pull に失敗しました" }

        $sidecar = Join-Path $Repo "sidecar"
        if (Test-Path (Join-Path $sidecar "package.json")) {
          Log "npm install (sidecar)"
          Push-Location $sidecar
          $out = & npm install 2>&1 | Out-String
          Pop-Location
          Log $out.Trim()
          if ($LASTEXITCODE -ne 0) { Fail "npm install に失敗しました。Node.js が入っているか確認してください。" }
        }

        $proj = Join-Path $Repo "src\ClaudeNote\ClaudeNote.csproj"
        Log "dotnet build $proj"
        $out = & dotnet build $proj -c Release --nologo 2>&1 | Out-String
        Log $out.Trim()
        if ($LASTEXITCODE -ne 0) { Fail "ビルドに失敗しました。.NET 9 SDK が入っているか確認してください。" }

        Log "ClaudeNote を起動します: $Exe"
        Start-Process -FilePath $Exe
        Log "更新が完了しました"
        Write-Host ""
        Write-Host "更新が完了しました。ClaudeNote を起動しました。" -ForegroundColor Green
        Start-Sleep -Seconds 3
        """;

    private readonly record struct GitResult(int ExitCode, string Output, string Error);

    private static GitResult Run(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return new GitResult(-1, "", "git を起動できませんでした。");
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return new GitResult(proc.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new GitResult(-1, "", ex.Message);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
