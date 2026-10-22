using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace MidiKeyPlayer.Engine;

public static class AutoUpdate
{
    private static readonly bool Enabled = true;

    private const string Owner = "ChickenD233";
    private const string Repo = "midikey-player";

    public static string ReleasesUrl => $"https://github.com/{Owner}/{Repo}/releases";

    public const string AuthorSpaceUrl = "https://space.bilibili.com/28440883?spm_id_from=333.1007.0.0";

    public static string AuthorSpaceUrlShort => AuthorSpaceUrl.Split('?')[0];

    public const string AfdianUrl = "https://afdian.com/a/chickending";

    public static string AfdianUrlShort => AfdianUrl.Replace("https://", "", StringComparison.Ordinal);

    public const string GitHubUrl = "https://github.com/ChickenD233";
    public static string GitHubUrlShort => GitHubUrl.Replace("https://", "", StringComparison.Ordinal);

    public const string QqGroupNumber = "1042477909";
    public const string QqGroupNumber2 = "671434610";

    public const string AuthorName = "Chicken丁";

    public static bool IsAuthorAnswer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (c is '-' or '_' or '·' or '.' or '。' or '、') continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        string s = sb.ToString();
        return s == "chicken丁" || s == "28440883";
    }

    public const string FreeNotice =
        "本软件免费开源，没有付费版本，作者从未销售本软件。"
        + "若你付费购买，请立即申请退款并举报卖家。"
        + "设置里的赞助是自愿打赏，不是付费购买。";

    public const string FreeNoticeShort =
        "本软件免费开源，没有付费版本。若你付费购买，请申请退款并举报卖家。";

    public const string QuizRefundNote =
        "本程序完全免费、开源。如果你是「购买」的此软件，立刻退款，你被骗了："
        + "到购买平台的订单里申请退款，并举报卖家。";

    public const string ResellerLine = "祝倒狗冚家富貴=）";

    public const string MandatoryMarker = "[强制更新]";

    public const string NoticeVersion = "1.3.0";

    public const string RequiredVersion = "1.0.32";

    public static bool IsRequiredVersion(string current) => IsNewer(RequiredVersion, current);

    private static readonly string[] AllowedUrlPrefixes =
    {
        $"https://github.com/{Owner}/{Repo}/",
    };

    private static bool IsAllowedUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        foreach (string prefix in AllowedUrlPrefixes)
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null) return "0.0.0";
            return $"{Math.Max(0, v.Major)}.{Math.Max(0, v.Minor)}.{Math.Max(0, v.Build)}";
        }
    }

    public sealed class Result
    {
        public bool HasUpdate { get; init; }
        public string LatestTag { get; init; } = "";
        public string CurrentTag { get; init; } = "";
        public string ReleaseName { get; init; } = "";
        public string ReleaseUrl { get; init; } = "";
        public string AssetUrl { get; init; } = "";
        public bool Skipped { get; init; }       
        public bool Mandatory { get; init; }
        public string? Error { get; init; }      
    }

    public static async Task<Result> CheckAsync(string? skippedTag = null,
                                                CancellationToken ct = default)
    {
        if (!Enabled) return new Result { CurrentTag = CurrentVersion };

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MidiKeyPlayer-UpdateCheck");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new Result { Error = $"HTTP {(int)resp.StatusCode}", CurrentTag = CurrentVersion };

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                                             .ConfigureAwait(false);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            string name = root.TryGetProperty("name", out var nm) ? (nm.GetString() ?? "") : "";
            string body = root.TryGetProperty("body", out var bd) ? (bd.GetString() ?? "") : "";
            string html = root.TryGetProperty("html_url", out var h) ? (h.GetString() ?? "") : "";

            if (html.Length > 0 && !IsAllowedUrl(html))
                Persist.LogFile.Append($"[更新] 接口返回的地址不在白名单，改用 Releases 页：{html}");

            string assetUrl = "";
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string an = a.TryGetProperty("name", out var anp) ? (anp.GetString() ?? "") : "";
                    string au = a.TryGetProperty("browser_download_url", out var aup) ? (aup.GetString() ?? "") : "";
                    if (an.StartsWith("MidiKeyPlayer-win-x64-", StringComparison.OrdinalIgnoreCase) &&
                        an.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                        IsAllowedAssetUrl(au))
                    {
                        assetUrl = au;
                        break;
                    }
                }
            }

            bool newer = IsNewer(tag, CurrentVersion);
            bool mandatory = newer && (body.Contains(MandatoryMarker, StringComparison.Ordinal)
                                       || name.Contains(MandatoryMarker, StringComparison.Ordinal));
            return new Result
            {
                HasUpdate = newer,
                LatestTag = tag.TrimStart('v', 'V'),
                CurrentTag = CurrentVersion,
                ReleaseName = name,
                ReleaseUrl = IsAllowedUrl(html) ? html : ReleasesUrl,
                AssetUrl = assetUrl,
                Mandatory = mandatory,
                Skipped = newer && !string.IsNullOrEmpty(skippedTag) &&
                          string.Equals(tag.TrimStart('v', 'V'), skippedTag.TrimStart('v', 'V'),
                                        StringComparison.OrdinalIgnoreCase)
            };
        }
        catch (Exception ex)
        {
            return new Result { Error = ex.GetType().Name, CurrentTag = CurrentVersion };
        }
    }

    public static bool IsNewer(string latest, string current)
    {
        var a = Parse(latest);
        var b = Parse(current);
        for (int i = 0; i < 3; i++)
        {
            if (a[i] != b[i]) return a[i] > b[i];
        }
        return false;
    }

    private static int[] Parse(string v)
    {
        var parts = (v ?? "").Trim().TrimStart('v', 'V').Split('.', '-', '+');
        var outv = new int[3];
        for (int i = 0; i < parts.Length; i++)
        {
            if (i >= 3) break;
            if (!int.TryParse(parts[i], out int n))
            {
                Persist.LogFile.Append($"[更新] 版本号「{v}」里的「{parts[i]}」不是数字，这一段按 0 算。");
                n = 0;
            }
            outv[i] = n;
        }
        return outv;
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Persist.LogFile.Append($"[更新] 打开链接失败（{url}）：{ex.Message}");
        }
    }

    private static string AssetUrlPrefix => $"https://github.com/{Owner}/{Repo}/releases/download/";

    private static bool IsAllowedAssetUrl(string url)
        => !string.IsNullOrEmpty(url) && url.StartsWith(AssetUrlPrefix, StringComparison.OrdinalIgnoreCase);

    private static string UpdateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MidiKeyPlayer", "update");

    public static async Task<string?> DownloadAsync(string url, IProgress<double>? progress = null,
                                                    CancellationToken ct = default)
    {
        if (!IsAllowedAssetUrl(url))
        {
            Persist.LogFile.Append($"[更新] 拒绝下载白名单外的地址：{url}");
            return null;
        }
        try
        {
            Directory.CreateDirectory(UpdateDir);
            string dest = Path.Combine(UpdateDir, Path.GetFileName(url.Split('?')[0]));
            string part = dest + ".part";

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MidiKeyPlayer-UpdateCheck");
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                         .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Persist.LogFile.Append($"[更新] 下载失败：HTTP {(int)resp.StatusCode}");
                return null;
            }

            long? total = resp.Content.Headers.ContentLength;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[64 * 1024];
                long got = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    got += n;
                    if (total is > 0) progress?.Report(Math.Clamp((double)got / total.Value, 0, 1));
                }
            }
            File.Move(part, dest, overwrite: true);
            return dest;
        }
        catch (OperationCanceledException)
        {
            return null;   
        }
        catch (Exception ex)
        {
            Persist.LogFile.Append($"[更新] 下载异常：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public static string? ExtractNewExe(string zipPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var exeEntry = zip.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName, "MidiKeyPlayer.exe", StringComparison.OrdinalIgnoreCase));
            bool hasChangelog = zip.Entries.Any(e =>
                e.FullName.EndsWith("更新日志.txt", StringComparison.Ordinal));
            if (exeEntry == null || !hasChangelog)
            {
                Persist.LogFile.Append(
                    $"[更新] 更新包内容不符（条目：{string.Join(", ", zip.Entries.Select(e => e.FullName))}）");
                return null;
            }

            string dest = Path.Combine(UpdateDir, "MidiKeyPlayer.new.exe");
            exeEntry.ExtractToFile(dest, overwrite: true);

            var fi = new FileInfo(dest);
            bool ok = fi.Length >= 5 * 1024 * 1024;
            if (ok)
            {
                using var fs = File.OpenRead(dest);
                ok = fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
            }
            if (!ok)
            {
                Persist.LogFile.Append($"[更新] 解出的 exe 校验不过（{fi.Length} 字节），按损坏处理。");
                try { File.Delete(dest); } catch { }
                return null;
            }
            return dest;
        }
        catch (Exception ex)
        {
            Persist.LogFile.Append($"[更新] 更新包校验失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public static bool StartUpdater(string newExePath)
    {
        try
        {
            string target = Environment.ProcessPath ?? "";
            if (target.Length == 0 || !File.Exists(target))
            {
                Persist.LogFile.Append("[更新] 取不到当前 exe 路径，不能自动更新。");
                return false;
            }
            if (!File.Exists(newExePath))
            {
                Persist.LogFile.Append($"[更新] 新 exe 不存在：{newExePath}");
                return false;
            }

            Directory.CreateDirectory(UpdateDir);
            string scriptPath = Path.Combine(UpdateDir, "apply-update.ps1");
            string Esc(string s) => s.Replace("'", "''");
            string script = string.Join("\r\n", new[]
            {
                "$ErrorActionPreference = 'SilentlyContinue'",
                $"$target = '{Esc(target)}'",
                $"$new    = '{Esc(newExePath)}'",
                $"$appPid = {Environment.ProcessId}",
                "try { Wait-Process -Id $appPid -Timeout 180 -ErrorAction Stop } catch {}",
                "Start-Sleep -Milliseconds 500",
                "$tmp = \"$target.tmp\"",
                "$bak = \"$target.bak\"",
                "$ok = $false",
                "for ($i = 0; $i -lt 120; $i++) {",
                "    try { Copy-Item -LiteralPath $new -Destination $tmp -Force -ErrorAction Stop; $ok = $true; break }",
                "    catch { Start-Sleep -Milliseconds 500 }",
                "}",
                "if ($ok) {",
                "    $fi = Get-Item -LiteralPath $tmp",
                "    if ($null -eq $fi -or $fi.Length -lt 10240) { $ok = $false }",
                "}",
                "if ($ok) {",
                "    try { Copy-Item -LiteralPath $target -Destination $bak -Force -ErrorAction Stop } catch { $ok = $false }",
                "}",
                "if ($ok) {",
                "    try { Move-Item -LiteralPath $tmp -Destination $target -Force -ErrorAction Stop } catch { $ok = $false }",
                "}",
                "if ($ok) {",
                "    Remove-Item -LiteralPath $bak -Force",
                "    Remove-Item -LiteralPath $new -Force",
                "    Start-Process -FilePath $target",
                "} else {",
                "    # 失败：清掉临时文件，有备份就把旧 exe 还原，并把旧版重新启动 ，",
                "    # 别让用户面对一个被关掉又打不开的程序",
                "    Remove-Item -LiteralPath $tmp -Force",
                "    if (Test-Path -LiteralPath $bak) {",
                "        try { Move-Item -LiteralPath $bak -Destination $target -Force -ErrorAction Stop } catch {}",
                "    }",
                "    if (Test-Path -LiteralPath $target) { Start-Process -FilePath $target }",
                "}",
                "Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force",
            });
            File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            Persist.LogFile.Append($"[更新] 启动更新脚本失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
