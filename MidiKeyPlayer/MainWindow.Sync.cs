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

    private int? _syncTranspose;

    internal List<int>? SyncVoiceMask => _syncVoiceMask;

    internal bool SyncMaskActive => _syncVoiceMask != null;

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

    internal void ClearSyncMaskForSettings()
    {
        CancelSyncLeadIn();   
        if (_syncVoiceMask == null && _syncTranspose == null) return;
        _syncVoiceMask = null;
        _syncTranspose = null;
        RefreshVoiceRolesForSettings();
    }

    internal int SyncVoiceCountForSettings() => _mixOrder.Count;

    internal string SyncVoiceNameForSettings(int index)
        => index >= 0 && index < _mixOrder.Count ? _mixOrder[index].Name : "";

    private void RefreshVoiceRolesForSettings()
    {
        UpdateVoiceRoles();
        RefreshPreview();
        UpdateTransportUi();
    }

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
            return "";   
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
        SyncMixOrder();   
        InsertLog($"远程同演：已把 {_mixOrder.Count} 条声轨按列表顺序编号（声部 1 到 {_mixOrder.Count}），全场编号一致。");
    }

    internal double SyncProgressSecondsForSettings()
    {
        if (_engine is { IsRunning: true }) return _engine.ElapsedSeconds;
        return SliderProgress?.Value ?? 0;
    }

    internal bool SyncPlayingForSettings => _engine is { IsRunning: true };

    private DispatcherTimer? _syncLeadTimer;

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
        if (_engine is { IsRunning: true }) return;   

        if (leadInMs > 0) WaitThenStartSync(positionSec, leadInMs, resume: false);
        else StartSyncPlaybackCore(positionSec);
    }

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

    internal void SeekSyncPlaybackForSettings(double positionSec)
    {
        if (_engine is { IsRunning: true })
        {
            double total = System.Math.Max(0.1, _engine.TotalSeconds);
            _engine.SeekFraction(System.Math.Clamp(positionSec / total, 0, 1));
            if (SliderProgress != null) SliderProgress.Value = System.Math.Clamp(positionSec, 0, total);
            return;
        }
        if (SliderProgress != null)
            SliderProgress.Value = System.Math.Clamp(positionSec, 0, System.Math.Max(0.1, SliderProgress.Maximum));
    }

    internal void SetSyncSpeedForSettings(double speed)
    {
        int percent = (int)System.Math.Clamp(speed * 100.0, 10, 400);
        if (SliderSpeed != null) SliderSpeed.Value = percent;   
    }

    internal void StopSyncPlaybackForSettings()
    {
        CancelSyncLeadIn();
        StopPlaybackNow();
    }

    internal AppConfig SyncConfigForSettings => _cfg;

    internal void InsertSyncLogForSettings(string message) => InsertLog(message);

    internal void SaveConfigForSettings() => _cfg.Save();

    internal Task<bool> CopyTextForSettingsAsync(Avalonia.Controls.Window owner, string text)
        => CopyTextToClipboard(owner, text);
}
