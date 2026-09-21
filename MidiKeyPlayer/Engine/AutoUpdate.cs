using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace MidiKeyPlayer.Engine;

/// <summary>后台静默检查 GitHub 新版本，有新版时界面提示并可跳转 Release 页。</summary>
public static class AutoUpdate
{
    /// <summary>
    /// 自动更新开关。仓库已转为公开（v1.0.9 起开启检查；私有库时期未带凭据的检查会返回 404）。
    /// 用 static readonly 而不是 const：const 为 false 时编译器会把后面整段检查代码
    /// 判成不可达并报 CS0162。
    /// </summary>
    private static readonly bool Enabled = true;

    private const string Owner = "ChickenD233";
    private const string Repo = "midikey-player";

    /// <summary>最新 Release 页面（用于跳转下载）。</summary>
    public static string ReleasesUrl => $"https://github.com/{Owner}/{Repo}/releases";

    // ================= 作者链接与免费声明 =================

    /// <summary>作者 B 站主页（点按钮打开的就是这个地址）。</summary>
    public const string AuthorSpaceUrl = "https://space.bilibili.com/28440883?spm_id_from=333.1007.0.0";

    /// <summary>界面上显示用的主页地址：去掉分享带的 ?spm… 尾巴，短一点、不换行。</summary>
    public static string AuthorSpaceUrlShort => AuthorSpaceUrl.Split('?')[0];

    /// <summary>作者的爱发电赞助页（赞助入口；界面按「赞助」而不是「购买」来写）。</summary>
    public const string AfdianUrl = "https://afdian.com/a/chickending";

    /// <summary>界面上显示用的爱发电地址：去掉协议的 https://，短一点、不换行。</summary>
    public static string AfdianUrlShort => AfdianUrl.Replace("https://", "", StringComparison.Ordinal);

    /// <summary>作者的 GitHub 主页（看源码、看 Release 都在这里）。</summary>
    public const string GitHubUrl = "https://github.com/ChickenD233";
    public static string GitHubUrlShort => GitHubUrl.Replace("https://", "", StringComparison.Ordinal);

    /// <summary>反馈 QQ 群号。</summary>
    public const string QqGroupNumber = "1042477909";

    /// <summary>作者在 B 站的名字：第一次使用时的验证题答案。</summary>
    public const string AuthorName = "Chicken丁";

    /// <summary>
    /// 第一次使用的验证题答案判定：忽略大小写与空白/分隔符，
    /// 「Chicken丁」「chicken 丁」「CHICKEN丁」都算过；填作者 B 站的数字 ID（主页地址里的那串）也算。
    /// 目的不是拦住谁，而是让盗卖者的买家停下来看到「这是免费软件」。
    /// </summary>
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

    /// <summary>
    /// 免费声明。有人把这个免费开源的程序拿去卖（闲鱼上一个 8.8 元，卖了几百上千单），
    /// 所以每个版本都要把这句话摆出来，并写清楚唯一发布渠道。
    /// 最后一句特意点明「赞助归作者、不是买软件」，免得有人把爱发电的赞助入口当成付费购买。
    /// </summary>
    public const string FreeNotice =
        "本程序完全免费、开源，没有收费版本，作者也从没卖过它。"
        + "唯一发布渠道：B 站（Chicken丁）、GitHub Releases、QQ 群 " + QqGroupNumber + "。"
        + "如果你是「购买」的此软件，立刻退款，你被骗了：到购买平台的订单里申请退款，并举报卖家。"
        + "设置里的赞助是自愿打赏给作者，不是购买，不影响程序功能。";

    /// <summary>
    /// 验证题上的一句话：只说免费与退款，**不提作者名字**（名字就是那道题的答案，写在题面上等于送答案）。
    /// </summary>
    public const string QuizRefundNote =
        "本程序完全免费、开源。如果你是「购买」的此软件，立刻退款，你被骗了："
        + "到购买平台的订单里申请退款，并举报卖家。";

    /// <summary>验证题下面单列的一行：送给倒卖的。</summary>
    public const string ResellerLine = "祝倒狗冚家富貴=）";

    /// <summary>
    /// 强制更新标记：最新 Release 的标题或说明里带这个串，就表示「低于该版本的程序必须更新」。
    /// 这样任何一版都能把「强制」发给还在用旧版的用户 —— 硬编码的强制线只能约束装着那条线的版本，
    /// 管不了更老的版本（老版本里根本没有这段代码）。
    /// 发版时在 Release 说明里加一行 [强制更新] 即可，见 tools\release.ps1 的 -Mandatory。
    /// </summary>
    public const string MandatoryMarker = "[强制更新]";

    /// <summary>
    /// 免费声明与链接的展示版本号：改这段文案时一起改，程序据此只弹一次通知。
    /// 1.1.0：免费声明浮层加了「爱发电」按钮，文案也点了赞助不等于购买 → 版本号加一，
    /// 让已经看过 1.0.32 那版的人再看一次，才拿得到新的赞助入口。
    /// 注意这里写的是不带 v 的三段号，与 csproj 的 &lt;Version&gt; 不是同一个字符串，但要对得上版本。
    /// </summary>
    public const string NoticeVersion = "1.1.0";

    /// <summary>
    /// 强制更新线：低于这个版本的实例必须更新到最新版才能继续用。
    /// 判定是「本常量比当前版本新」，所以装着本常量版本或更新版本的实例不受影响。
    /// 想解除强制更新：把本常量改成当前版本号，重新发一版即可。
    /// </summary>
    public const string RequiredVersion = "1.0.32";

    /// <summary>当前版本是否低于强制更新线（低 = 必须更新）。</summary>
    public static bool IsRequiredVersion(string current) => IsNewer(RequiredVersion, current);

    /// <summary>允许跳转的地址前缀。只有本仓库的页面才交给系统浏览器打开。</summary>
    private static readonly string[] AllowedUrlPrefixes =
    {
        $"https://github.com/{Owner}/{Repo}/",
    };

    /// <summary>地址是否属于本仓库。不在白名单就退回 <see cref="ReleasesUrl"/>，不交给 shell。</summary>
    private static bool IsAllowedUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        foreach (string prefix in AllowedUrlPrefixes)
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 当前程序版本（如 1.0.7，第三段必须取 Build，第四段是内部版本号）。
    /// 版本号只支持三段数字（<c>x.y.z</c>）。预发布后缀（<c>1.0.0-rc.1</c> 里的 <c>-rc.1</c>）
    /// 不参与比较，会被丢弃，所以它等于 <c>1.0.0</c>。写 tag 时请只用 <c>vX.Y.Z</c>。
    /// </summary>
    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null) return "0.0.0";
            // 未显式指定版本时 Build/Revision 可能是 -1，用 Math.Max 兜底
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
        /// <summary>更新包（zip）直链；找不到或地址不在白名单时为空串，界面退回手动下载。</summary>
        public string AssetUrl { get; init; } = "";
        public bool Skipped { get; init; }       // 用户选了"跳过这个版本"
        /// <summary>最新版要求强制更新：说明里带 <see cref="MandatoryMarker"/>，跳过选项不生效。</summary>
        public bool Mandatory { get; init; }
        public string? Error { get; init; }      // 网络失败等（静默处理，不打扰用户）
    }

    /// <summary>查询最新 Release 并与当前版本比较；最新版正好是被跳过的那版则 HasUpdate=false。</summary>
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

            // 地址白名单：接口返回的 html_url 只认本仓库前缀，其它一律退回固定的 Releases 页，
            // 免得将来换了数据源以后把 file:/ms-*: 之类的地址直接交给 shell。
            if (html.Length > 0 && !IsAllowedUrl(html))
                Persist.LogFile.Append($"[更新] 接口返回的地址不在白名单，改用 Releases 页：{html}");

            // 更新包直链：从 Release 资产里找 MidiKeyPlayer-win-x64-*.zip，
            // 地址同样要过白名单（只认本仓库 releases/download/ 前缀），找不到就是空串，
            // 界面据此退回「打开下载页」的手动流程。
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
            // 强制更新：最新版说明（或标题）里带标记 → 低于它的版本必须更新，跳过选项作废
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
            // 没网 / 被墙 / 超时都属正常，静默忽略
            return new Result { Error = ex.GetType().Name, CurrentTag = CurrentVersion };
        }
    }

    /// <summary>版本号比较：latest 是否比 current 新（按 x.y.z 逐段数值比较）。</summary>
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
            // 只比较前三段。预发布段（1.0.0-rc.1 里的 rc）既不参与比较，也不尝试解析 ——
            // 否则每个预发布标签都会白写一条"不是数字"的警告。
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

    /// <summary>用系统默认浏览器打开链接（跳转到 Release 页面下载）。</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // 打不开就算了，界面上也会把链接文字显示出来供手动复制；失败原因写日志
            Persist.LogFile.Append($"[更新] 打开链接失败（{url}）：{ex.Message}");
        }
    }

    // ================= 自动下载与替换 =================

    /// <summary>允许下载的更新包地址前缀：只认本仓库的 releases/download/。</summary>
    private static string AssetUrlPrefix => $"https://github.com/{Owner}/{Repo}/releases/download/";

    /// <summary>更新包地址是否合法（白名单前缀，且必须是 https）。</summary>
    private static bool IsAllowedAssetUrl(string url)
        => !string.IsNullOrEmpty(url) && url.StartsWith(AssetUrlPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>下载与解包目录：%LOCALAPPDATA%\MidiKeyPlayer\update。</summary>
    private static string UpdateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MidiKeyPlayer", "update");

    /// <summary>
    /// 把更新包下载到本地（<see cref="UpdateDir"/>）。返回 zip 路径；失败或被取消返回 null（原因写日志）。
    /// 只允许白名单里的本仓库资产地址。先写 .part 临时文件、下完再改名：
    /// 中途断网/关程序留下的半截文件不会被当成完整包。
    /// </summary>
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
            return null;   // 关程序时取消：正常路径，不写日志
        }
        catch (Exception ex)
        {
            Persist.LogFile.Append($"[更新] 下载异常：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 校验更新包并解出新 exe：包内必须有 MidiKeyPlayer.exe 与 更新日志.txt；
    /// exe 必须是 PE（MZ 头）且大于 5 MB。返回解出的新 exe 路径；校验不过返回 null（原因写日志）。
    /// </summary>
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

    /// <summary>
    /// 启动更新脚本并返回 true：脚本等本进程退出后，把新 exe 覆盖到当前程序路径并重启新版。
    /// 调用方随后应当走正常退出流程（保存设置、松按键、注销热键）。
    /// 当前 exe 路径取不到、新 exe 不存在时不写脚本，返回 false。
    /// </summary>
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
                // 等主程序退出（保存设置、松按键、注销热键都在退出流程里）
                "try { Wait-Process -Id $appPid -Timeout 180 -ErrorAction Stop } catch {}",
                "Start-Sleep -Milliseconds 500",
                "$tmp = \"$target.tmp\"",
                "$bak = \"$target.bak\"",
                // 先把新 exe 复制到同目录的临时文件：直接原地覆盖目标时一旦中断（断电、被杀），
                // 装好的程序就变砖。exe 文件可能还被系统占用一小会：重试一分钟
                "$ok = $false",
                "for ($i = 0; $i -lt 120; $i++) {",
                "    try { Copy-Item -LiteralPath $new -Destination $tmp -Force -ErrorAction Stop; $ok = $true; break }",
                "    catch { Start-Sleep -Milliseconds 500 }",
                "}",
                // 校验临时文件确实存在且不是残片（小于 10KB 视为复制失败）
                "if ($ok) {",
                "    $fi = Get-Item -LiteralPath $tmp",
                "    if ($null -eq $fi -or $fi.Length -lt 10240) { $ok = $false }",
                "}",
                // 替换前先把当前 exe 备份到 .bak：替换失败还能把旧版还原回来
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
                "    # 失败：清掉临时文件，有备份就把旧 exe 还原，并把旧版重新启动 ——",
                "    # 别让用户面对一个被关掉又打不开的程序",
                "    Remove-Item -LiteralPath $tmp -Force",
                "    if (Test-Path -LiteralPath $bak) {",
                "        try { Move-Item -LiteralPath $bak -Destination $target -Force -ErrorAction Stop } catch {}",
                "    }",
                "    if (Test-Path -LiteralPath $target) { Start-Process -FilePath $target }",
                "}",
                "Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force",
            });
            // Windows PowerShell 5.1 按 BOM 判断脚本编码：写 UTF-8 BOM，路径里的中文才不乱码
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
