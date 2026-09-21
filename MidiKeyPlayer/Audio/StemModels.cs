using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MidiKeyPlayer.Audio;

/// <summary>
/// 人声 / 伴奏分离模型（Spleeter 2-stem）的获取与管理。
///
/// 模型合计约 55 MB，不塞进 exe（exe 里只放 230 KB 的 basic-pitch）。首次用到时按需下载，
/// 落在 <c>%LOCALAPPDATA%\MidiKeyPlayer\models\</c>，之后离线可用。
///
/// 官方发布物是一个 tar.bz2，里面两个 fp16 的 ONNX。解包用 Windows 自带的
/// <c>tar.exe</c>（Win10 1803 起自带 bsdtar，能解 bzip2）。
/// </summary>
internal static class StemModels
{
    /// <summary>官方发布物地址（k2-fsa/sherpa-onnx 的 source-separation-models）。</summary>
    private const string ArchiveUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/source-separation-models/sherpa-onnx-spleeter-2stems-fp16.tar.bz2";

    /// <summary>允许下载的地址前缀（只认这一处，避免被改成任意地址）。</summary>
    private static readonly string[] AllowedPrefixes =
    {
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/source-separation-models/",
    };

    /// <summary>模型目录：%LOCALAPPDATA%\MidiKeyPlayer\models。</summary>
    public static string ModelDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MidiKeyPlayer", "models");

    public static string VocalsPath => Path.Combine(ModelDir, "vocals.fp16.onnx");
    public static string AccompanimentPath => Path.Combine(ModelDir, "accompaniment.fp16.onnx");

    /// <summary>两个模型都在本地。</summary>
    public static bool IsReady => File.Exists(VocalsPath) && File.Exists(AccompanimentPath);

    /// <summary>下载并解包模型。progress 收到 0..1；失败抛异常，消息可直接给用户看。</summary>
    public static async Task EnsureAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (IsReady) { progress?.Report(1.0); return; }

        if (!AllowedPrefixes.AnyPrefix(ArchiveUrl))
            throw new InvalidOperationException("模型地址不在白名单里");

        Directory.CreateDirectory(ModelDir);
        string archive = Path.Combine(ModelDir, "spleeter-2stems-fp16.tar.bz2");
        string part = archive + ".part";

        // 1) 下载（先写 .part，下完再改名：半截文件不会被当成完整包）
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MidiKeyPlayer-StemModels");
            using var resp = await http.GetAsync(ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                                        .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"下载失败：HTTP {(int)resp.StatusCode}");

            long? total = resp.Content.Headers.ContentLength;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[128 * 1024];
                long got = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    got += n;
                    if (total is > 0) progress?.Report(Math.Clamp((double)got / total.Value * 0.9, 0, 0.9));
                }
            }
            File.Move(part, archive, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            TryDelete(part);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(part);
            throw new InvalidOperationException("分离模型下载失败（需要联网一次，之后离线可用）：" + ex.Message, ex);
        }

        // 2) 解包（用 Windows 自带的 tar）
        progress?.Report(0.92);
        string temp = Path.Combine(ModelDir, "unpack");
        try
        {
            Directory.CreateDirectory(temp);
            var psi = new ProcessStartInfo("tar.exe")
            {
                WorkingDirectory = temp,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-xjf");
            psi.ArgumentList.Add(archive);
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("调不起 tar.exe");
            string errors = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"解包失败（tar 退出码 {proc.ExitCode}）：{errors.Trim()}");

            // 3) 把两个 onnx 挪到模型目录（发布物里在子目录下）
            foreach (string file in Directory.GetFiles(temp, "*.onnx", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                string dest = Path.Combine(ModelDir, name);
                File.Move(file, dest, overwrite: true);
            }
            if (!IsReady)
                throw new InvalidOperationException("解包后没找到 vocals.fp16.onnx 与 accompaniment.fp16.onnx");
        }
        finally
        {
            TryDeleteDirectory(temp);
            TryDelete(archive);
        }

        progress?.Report(1.0);
    }

    /// <summary>删掉已下载的模型，返回释放的字节数（供设置里的「清理模型」用）。</summary>
    public static long RemoveAll()
    {
        long freed = 0;
        foreach (string file in new[] { VocalsPath, AccompanimentPath })
        {
            try
            {
                if (!File.Exists(file)) continue;
                freed += new FileInfo(file).Length;
                File.Delete(file);
            }
            catch { }
        }
        return freed;
    }

    /// <summary>两个模型占用的字节数。</summary>
    public static long SizeOnDisk()
    {
        long size = 0;
        foreach (string file in new[] { VocalsPath, AccompanimentPath })
        {
            try { if (File.Exists(file)) size += new FileInfo(file).Length; } catch { }
        }
        return size;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private static bool AnyPrefix(this string[] prefixes, string url)
    {
        foreach (string p in prefixes)
            if (url.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
