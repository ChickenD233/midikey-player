using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Avalonia.Threading;
using MidiKeyPlayer.Engine;
using MidiKeyPlayer.Midi;
using MidiKeyPlayer.Persist;

namespace MidiKeyPlayer;

public partial class MainWindow
{
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
        CancelSyncLeadIn();   // 退出房间时把还没到点的开演等待一起取消
        if (_syncVoiceMask == null && _syncTranspose == null) return;
        _syncVoiceMask = null;
        _syncTranspose = null;
        RefreshVoiceRolesForSettings();
    }

    /// <summary>设置页用：声部总数（= 当前参与演奏的声轨数）。</summary>
    internal int SyncVoiceCountForSettings() => _mixOrder.Count;

    /// <summary>设置页用：第 index 个声部的显示名。越界返回空串。</summary>
    internal string SyncVoiceNameForSettings(int index)
        => index >= 0 && index < _mixOrder.Count ? _mixOrder[index].Name : "";

    /// <summary>设置页用：按新的声部掩码重算"参与演奏 / 声部序号"与卷帘配色。</summary>
    private void RefreshVoiceRolesForSettings()
    {
        UpdateVoiceRoles();
        RefreshPreview();
        UpdateTransportUi();
    }

    // ================= 曲目信息 =================

    /// <summary>设置页用：当前 MIDI 文件路径（空 = 没载入）。</summary>
    internal string SyncTrackPathForSettings => _parsed?.FilePath ?? "";

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

    internal (string Fingerprint, double DurationSec, List<string> VoiceNames)? SyncTrackInfoForSettings()
    {
        string fp = SyncTrackFingerprintForSettings();
        if (fp.Length == 0 || _parsed == null) return null;
        var names = new List<string>();
        foreach (var row in ActiveRows()) names.Add(row.Name);
        return (fp, _parsed.DurationSec, names);
    }

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

    private DispatcherTimer? _syncLeadTimer;

    /// <summary>取消还没到点的开演等待（暂停 / 停止 / 退出房间时用）。</summary>
    private void CancelSyncLeadIn()
    {
        _syncLeadTimer?.Stop();
        _syncLeadTimer = null;
    }

    internal void StartSyncPlaybackForSettings(double positionSec, int leadInMs = 0)
    {
        if (_engine is { IsRunning: true, IsPaused: true })
        {
            if (leadInMs > 0) WaitThenStartSync(positionSec, leadInMs, resume: true);
            else ResumeSyncPlaybackForSettings();
            return;
        }
        if (_engine is { IsRunning: true }) return;   // 真的在弹，不重开

        if (leadInMs > 0) WaitThenStartSync(positionSec, leadInMs, resume: false);
        else StartSyncPlaybackCore(positionSec);
    }

    /// <summary>等到开演时刻再动作（成员侧）。等待期间再收到暂停 / 停止会取消这次等待。</summary>
    private void WaitThenStartSync(double positionSec, int leadInMs, bool resume)
    {
        CancelSyncLeadIn();
        double sec = leadInMs / 1000.0;
        InsertLog($"远程同演：{sec:F1} 秒后开始（等全场落在同一拍上）。");
        LblStatus.Foreground = WarningBrush;
        LblStatus.Text = $"远程同演：{sec:F1} 秒后开始…";

        _syncLeadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, leadInMs)) };
        _syncLeadTimer.Tick += (_, _) =>
        {
            CancelSyncLeadIn();
            if (resume) ResumeSyncPlaybackForSettings();
            else StartSyncPlaybackCore(positionSec);
        };
        _syncLeadTimer.Start();
    }

    private void StartSyncPlaybackCore(double positionSec)
    {
        if (positionSec > 0.05 && SliderProgress != null)
        {
            SliderProgress.Value = System.Math.Clamp(positionSec, 0, SliderProgress.Maximum);
        }
        RequestPlay(skipCountdown: true);
    }

    internal void PauseSyncPlaybackForSettings()
    {
        CancelSyncLeadIn();
        if (_engine is { IsRunning: true, IsPaused: false })
        {
            _engine.Pause();
            SetPauseUi(true);
        }
        else
        {
            UpdateTransportUi();
        }
    }

    /// <summary>设置页用：继续。同样走主窗那条链路，状态与按钮一起回到「演奏中」。</summary>
    internal void ResumeSyncPlaybackForSettings()
    {
        if (_engine is { IsRunning: true, IsPaused: true })
        {
            _engine.Resume();
            SetPauseUi(false);
        }
        else
        {
            UpdateTransportUi();
        }
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

    internal void StopSyncPlaybackForSettings()
    {
        CancelSyncLeadIn();
        StopPlaybackNow();
    }

    // ================= 给设置页用的小接口 =================

    internal AppConfig SyncConfigForSettings => _cfg;

    /// <summary>设置页往主窗日志里写一行。</summary>
    internal void InsertSyncLogForSettings(string message) => InsertLog(message);

    /// <summary>设置页改完配置后落盘。</summary>
    internal void SaveConfigForSettings() => _cfg.Save();

    /// <summary>设置页用：复制一段文本到剪贴板（与赞助页复制群号同一条链路）。</summary>
    internal Task<bool> CopyTextForSettingsAsync(Avalonia.Controls.Window owner, string text)
        => CopyTextToClipboard(owner, text);
}
