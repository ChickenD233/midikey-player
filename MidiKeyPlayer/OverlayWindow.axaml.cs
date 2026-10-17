using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MidiKeyPlayer.Engine;

namespace MidiKeyPlayer;

public partial class OverlayWindow : Window
{
    /// <summary>拖动松手后触发：参数是新的窗口位置（供主窗写进设置）。</summary>
    public event Action<int, int>? DragFinished;

    /// <summary>点了浮窗上的 ✕：主窗把悬浮窗开关关掉（等同设置里取消勾选，写进设置）。</summary>
    public event Action? CloseRequested;

    private static readonly IBrush NoteBrush = new SolidColorBrush(Color.Parse("#FF4CAF7D"));
    private const int MaxDrawnNotes = 2000;   // 音符太多时只画前这些，防止控件树爆炸

    // 滚动窗口：可见 8 秒，播放头固定在 25% 处（左 2 秒已弹过，右 6 秒待弹）
    private const double WindowSec = 8.0;
    private const double PlayheadFrac = 0.25;

    private bool _positioned;
    private double _pxPerSec = 1.0;   // SetNotes 时按窗口宽度算好，ShowProgress 滚动用

    private double _lastElapsed;      // 最后一次推送的演奏进度（秒）
    private long _lastPushTicks;      // 最后一次推送的时间戳
    private double _pushRate = 1.0;   // 实测推进速率（播放速度 400% 时约 4）
    private bool _paused;
    private DispatcherTimer? _smoother;   // 30fps 外推定时器

    /// <summary>卷帘内容层的平移变换（XAML 里声明的那一个）。</summary>
    private TranslateTransform RollShift => (TranslateTransform)RollContent.RenderTransform!;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        UnhookRender();   // 关掉后不再占渲染帧回调
        base.OnClosed(e);
    }

    public void ShowCountdown(int secondsLeft, string nextLabel = "")
    {
        UnhookRender();   // 倒计时没有卷帘，不用平滑外推
        PanelCountdown.IsVisible = true;
        PanelProgress.IsVisible = false;
        TxtCountdown.Text = secondsLeft.ToString();
        TxtCountdownNext.Text = nextLabel;
        TxtCountdownNext.IsVisible = nextLabel.Length > 0;
        EnsureShown();
    }

    public void SetQueueHint(string text)
    {
        TxtQueue.Text = text;
        TxtQueue.IsVisible = text.Length > 0;
    }

    public void SetNotes(IReadOnlyList<MappedNote> notes, double totalSec)
    {
        RollContent.Children.Clear();

        double w = RollCanvas.Width, h = RollCanvas.Height;
        double total = Math.Max(0.1, totalSec);
        _pxPerSec = w / WindowSec;
        Canvas.SetLeft(Playhead, w * PlayheadFrac);
        if (notes.Count == 0) return;

        int minP = int.MaxValue, maxP = int.MinValue;
        foreach (var n in notes)
        {
            if (n.Pitch < minP) minP = n.Pitch;
            if (n.Pitch > maxP) maxP = n.Pitch;
        }
        int rows = Math.Max(1, maxP - minP + 1);
        double rowH = h / rows;

        int limit = Math.Min(notes.Count, MaxDrawnNotes);
        for (int i = 0; i < limit; i++)
        {
            var n = notes[i];
            var r = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = Math.Max(1.5, (n.End - n.Start) * _pxPerSec),
                Height = Math.Max(2.0, rowH - 0.8),
                Fill = NoteBrush,
                RadiusX = 1,
                RadiusY = 1,
            };
            Canvas.SetLeft(r, Math.Max(0, n.Start) * _pxPerSec);
            Canvas.SetTop(r, (maxP - n.Pitch) * rowH);
            RollContent.Children.Add(r);
        }
    }

    /// <summary>显示演奏进度：内容层向左滚动，播放头固定在 25% 处。</summary>
    public void ShowProgress(double elapsed, double total, int loopCount, bool paused)
    {
        PanelCountdown.IsVisible = false;
        PanelProgress.IsVisible = true;
        Bar.Maximum = Math.Max(0.1, total);
        Bar.Value = Math.Clamp(elapsed, 0, Bar.Maximum);

        long now = Stopwatch.GetTimestamp();
        if (!_paused && !paused)
        {
            double wall = (now - _lastPushTicks) / (double)Stopwatch.Frequency;
            double d = elapsed - _lastElapsed;
            if (wall > 0.01 && d >= 0 && d <= wall * 2.0 + 0.5)
                _pushRate = Math.Clamp(d / wall, 0.0, 5.0);
        }
        _lastElapsed = elapsed;
        _lastPushTicks = now;
        _paused = paused;
        HookRender();

        RollShift.X = RollCanvas.Width * PlayheadFrac - elapsed * _pxPerSec;
        TxtTime.Text = $"{elapsed:F1} / {total:F1} s";

        string state = "";
        if (paused) state = "已暂停";
        if (loopCount > 0) state += (state.Length > 0 ? " · " : "") + $"第 {loopCount + 1} 遍";
        TxtState.Text = state;
        EnsureShown();
    }

    private void SmoothTick(object? sender, EventArgs e)
    {
        if (_paused || !IsVisible || !PanelProgress.IsVisible) return;
        double shown = _lastElapsed
                       + _pushRate * (Stopwatch.GetTimestamp() - _lastPushTicks) / (double)Stopwatch.Frequency;
        // 外推最多比最后一次推送多 0.4 秒：推送万一断流（掉帧、卡顿），卷帘原地等，不往前冲
        shown = Math.Min(shown, _lastElapsed + 0.4);
        RollShift.X = RollCanvas.Width * PlayheadFrac - shown * _pxPerSec;
    }

    private void HookRender()
    {
        if (_smoother != null) return;
        _smoother = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _smoother.Tick += SmoothTick;
        _smoother.Start();
    }

    private void UnhookRender()
    {
        if (_smoother == null) return;
        _smoother.Stop();
        _smoother.Tick -= SmoothTick;
        _smoother = null;
    }

    /// <summary>恢复上次拖到的位置；(-1, -1) 或屏幕外 = 默认屏幕右上角。</summary>
    public void RestorePosition(int x, int y)
    {
        if (_positioned) return;
        _positioned = true;

        if (Screens.Primary?.WorkingArea is not { } wa) return;

        // 先量出内容尺寸：刚 Show 时 Bounds 可能还是 0，用近似值兜底
        double w = Math.Max(Bounds.Width, 120);
        double h = Math.Max(Bounds.Height, 48);

        bool valid = x >= wa.X && y >= wa.Y && x + w <= wa.Right + 40 && y + h <= wa.Bottom + 40;
        if (valid)
        {
            Position = new Avalonia.PixelPoint(x, y);
        }
        else
        {
            Position = new Avalonia.PixelPoint((int)(wa.Right - w - 16), wa.Y + 16);
        }
    }

    private void EnsureShown()
    {
        if (!IsVisible) Show();
    }

    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);   // 系统级拖动：松手在 PointerReleased 里回报位置
    }

    private void Card_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left)
            DragFinished?.Invoke(Position.X, Position.Y);
    }

    private void BtnOverlayOff_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
    }
}
