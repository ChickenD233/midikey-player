using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MidiKeyPlayer.Engine;
using MidiKeyPlayer.Midi;
using MidiKeyPlayer.Persist;

namespace MidiKeyPlayer;

/// <summary>
/// 主窗与「实验功能」页（远程同演）之间的接点。
///
/// 为什么单独一个文件：设置页要用主窗的一堆私有状态（当前曲目、声部分配、播放引擎、进度条），
/// 但两边不该互相摸内部成员。这里只开出一小组明确的方法，设置页只调这些。
///
/// 远程模式改了主窗的两件事：
/// 1. <see cref="SyncVoiceMask"/> 非空时，<see cref="ActiveRows"/> 只返回那些声部。
///    没它的话，每个人都会把整首曲子全弹一遍 —— 那就不是"分工"了。
/// 2. <see cref="SyncTransposeOverride"/> 非空时，<see cref="CurrentTranspose"/> 用它。
///    移调按人锁定，不再用界面上那个滑块。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 远程同演的声部掩码。null = 不在远程模式（主窗自己那套勾选照旧）。
    /// 空列表 = 在远程模式但还没分到声部（这个人这会儿不弹任何音）。
    /// </summary>
    private List<int>? _syncVoiceMask;

    /// <summary>远程同演的移调（半音）。null = 不在远程模式，用界面滑块的值。</summary>
    private int? _syncTranspose;

    /// <summary>远程模式下的声部掩码（测试与界面读取用）。</summary>
    internal List<int>? SyncVoiceMask => _syncVoiceMask;

    /// <summary>远程模式是否已接管声部与移调。</summary>
    internal bool SyncMaskActive => _syncVoiceMask != null;

    /// <summary>设置页用：改某个成员（一般就是自己）的声部与移调。</summary>
    internal void ApplySyncMaskForSettings(IReadOnlyList<int> voices, int transpose)
    {
        var mask = voices.ToList();
        bool changed = _syncVoiceMask == null
            || _syncTranspose != transpose
            || !_syncVoiceMask.SequenceEqual(mask);
        _syncVoiceMask = mask;
        _syncTranspose = transpose;
        if (changed) RefreshVoiceRolesForSettings();
    }

    /// <summary>设置页用：退出远程模式，把声部与移调还给界面自己管。</summary>
    internal void ClearSyncMaskForSettings()
    {
        if (_syncVoiceMask == null && _syncTranspose == null) return;
        _syncVoiceMask = null;
        _syncTranspose = null;
        RefreshVoiceRolesForSettings();
    }

    /// <summary>按新的声部掩码重算"参与演奏 / 声部序号"与卷帘配色。</summary>
    private void RefreshVoiceRolesForSettings()
    {
        UpdateVoiceRoles();
        RefreshPreview();
        UpdateTransportUi();
    }

    // ================= 曲目信息 =================

    /// <summary>设置页用：当前 MIDI 文件路径（空 = 没载入）。</summary>
    internal string SyncTrackPathForSettings => _parsed?.FilePath ?? "";

    /// <summary>
    /// 设置页用：当前曲目的指纹，取文件字节的 SHA-256 前 16 位十六进制。
    /// 用文件字节而不是解析结果：解析结果里有很多"怎么看都一样"的字段，
    /// 两份不同的文件很容易解析出相同的结果，那 fingerprint 就失去意义了。
    /// </summary>
    internal string SyncTrackFingerprintForSettings()
    {
        string path = SyncTrackPathForSettings;
        if (path.Length == 0 || !File.Exists(path)) return "";
        try
        {
            using var stream = File.OpenRead(path);
            byte[] hash = SHA256.HashData(stream);
            return System.Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
        }
        catch (IOException)
        {
            return "";   // 文件被占用 / 读不动：当作没有指纹，界面上会提示
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>
    /// 设置页用：曲目指纹、总时长、声部名表。主机开演前把它们发给全房间。
    ///
    /// 声部名表的口径**必须与声部序号一致**：序号 0 起的顺序就是
    /// <see cref="ActiveRows"/> 的顺序（勾选顺序，没勾时是被点选的那一行）。
    /// 所以开演前主窗要先把全部候选按列表顺序勾进合奏 —— 见
    /// <see cref="PrepareSyncTracksForSettings"/>。
    /// </summary>
    internal (string Fingerprint, double DurationSec, List<string> VoiceNames)? SyncTrackInfoForSettings()
    {
        string fp = SyncTrackFingerprintForSettings();
        if (fp.Length == 0 || _parsed == null) return null;
        var names = new List<string>();
        foreach (var row in ActiveRows()) names.Add(row.Name);
        return (fp, _parsed.DurationSec, names);
    }

    /// <summary>
    /// 设置页用：把所有候选轨按列表顺序勾进合奏，让声部编号全场一致。
    ///
    /// 为什么必须做：声部序号来自"勾选顺序"。每个人自己勾，勾的顺序就可能不同，
    /// 于是同一个序号在不同机器上指向不同的轨 —— 那样"每人一个声部"根本对不上。
    /// 统一按文件里的轨顺序编号之后，序号就只由文件决定，全场一致。
    /// </summary>
    internal void PrepareSyncTracksForSettings()
    {
        if (_tracks.Count == 0) return;
        foreach (var row in _tracks) row.IsMix = true;
        _mixOrder.Clear();
        foreach (var row in _tracks) _mixOrder.Add(row);
        SyncMixOrder();   // 与用户手点勾选框同一条收尾链路：编号、配色、谱面一起刷新
        InsertLog($"远程同演：已把 {_mixOrder.Count} 条声轨按列表顺序编号（声部 1 到 {_mixOrder.Count}），全场编号一致。");
    }

    // ================= 播放控制（设置页驱动） =================

    /// <summary>设置页用：当前播放头位置（秒）。没在播就是进度条的位置。</summary>
    internal double SyncProgressSecondsForSettings()
    {
        if (_engine is { IsRunning: true }) return _engine.ElapsedSeconds;
        return SliderProgress?.Value ?? 0;
    }

    /// <summary>设置页用：本机是否正在演奏。</summary>
    internal bool SyncPlayingForSettings => _engine is { IsRunning: true };

    /// <summary>设置页用：按远程同演定好的时刻开始演奏。不走倒计时（时刻已经定好了）。</summary>
    internal void StartSyncPlaybackForSettings(double positionSec)
    {
        if (_engine is { IsRunning: true }) return;   // 已经在弹就不重开
        if (positionSec > 0.05 && SliderProgress != null)
        {
            SliderProgress.Value = System.Math.Clamp(positionSec, 0, SliderProgress.Maximum);
        }
        RequestPlay(skipCountdown: true);
    }

    /// <summary>设置页用：暂停。</summary>
    internal void PauseSyncPlaybackForSettings()
    {
        if (_engine is { IsRunning: true, IsPaused: false }) _engine.Pause();
        UpdateTransportUi();
    }

    /// <summary>设置页用：继续。</summary>
    internal void ResumeSyncPlaybackForSettings()
    {
        if (_engine is { IsRunning: true, IsPaused: true }) _engine.Resume();
        UpdateTransportUi();
    }

    /// <summary>设置页用：把播放头挪到指定秒。拖进度条走的是同一条链路。</summary>
    internal void SeekSyncPlaybackForSettings(double positionSec)
    {
        if (_engine is { IsRunning: true })
        {
            double total = System.Math.Max(0.1, _engine.TotalSeconds);
            _engine.SeekFraction(System.Math.Clamp(positionSec / total, 0, 1));
            if (SliderProgress != null) SliderProgress.Value = System.Math.Clamp(positionSec, 0, total);
            return;
        }
        // 没在弹：只挪进度条，等开演时从那里开始
        if (SliderProgress != null)
            SliderProgress.Value = System.Math.Clamp(positionSec, 0, System.Math.Max(0.1, SliderProgress.Maximum));
    }

    /// <summary>设置页用：改演奏速度（主机改速度时全场一起改）。</summary>
    internal void SetSyncSpeedForSettings(double speed)
    {
        int percent = (int)System.Math.Clamp(speed * 100.0, 10, 400);
        if (SliderSpeed != null) SliderSpeed.Value = percent;   // 走滑块：界面数字与实际速度一致
    }

    /// <summary>设置页用：停止演奏。</summary>
    internal void StopSyncPlaybackForSettings()
    {
        if (_engine is { IsRunning: true }) _engine.Stop();
        UpdateTransportUi();
    }

    /// <summary>设置页用：设漂移微调倍率（不改界面上的速度数字）。</summary>
    internal void SetSyncTrimForSettings(double trim)
    {
        if (_engine != null) _engine.TrimRate = trim;
    }

    // ================= 给设置页用的小接口 =================

    /// <summary>
    /// 设置页读配置用。设置页要记成员名、成员号、代理地址，
    /// 但配置对象在 MainWindow 里，所以这里开一条只读的口子。
    /// </summary>
    internal AppConfig SyncConfigForSettings => _cfg;

    /// <summary>设置页往主窗日志里写一行。</summary>
    internal void InsertSyncLogForSettings(string message) => InsertLog(message);

    /// <summary>设置页改完配置后落盘。</summary>
    internal void SaveConfigForSettings() => _cfg.Save();

    /// <summary>设置页用：复制一段文本到剪贴板（与赞助页复制群号同一条链路）。</summary>
    internal Task<bool> CopyTextForSettingsAsync(Avalonia.Controls.Window owner, string text)
        => CopyTextToClipboard(owner, text);
}
