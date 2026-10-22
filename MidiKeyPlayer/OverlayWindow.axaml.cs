using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MidiKeyPlayer.Engine;

namespace MidiKeyPlayer;

public partial class OverlayWindow : Window
{
    public event Action<int, int>? DragFinished;

    public event Action? CloseRequested;

    private static readonly IBrush NoteBrush = new SolidColorBrush(Color.Parse("#FF4CAF7D"));
    private const int MaxDrawnNotes = 2000;   

    private const double WindowSec = 8.0;
    private const double PlayheadFrac = 0.25;

    private bool _positioned;
    private double _pxPerSec = 1.0;   

    private double _lastElapsed;      
    private long _lastPushTicks;      
    private double _pushRate = 1.0;   
    private bool _paused;
    private DispatcherTimer? _smoother;   

    private TranslateTransform RollShift => (TranslateTransform)RollContent.RenderTransform!;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        UnhookRender();   
        base.OnClosed(e);
    }

    public void ShowCountdown(int secondsLeft, string nextLabel = "")
    {
        UnhookRender();   
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

    public void SetSong(string name)
    {
        TxtSong.Text = name;
        TxtSong.IsVisible = name.Length > 0;
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

    public void RestorePosition(int x, int y)
    {
        if (_positioned) return;
        _positioned = true;

        if (Screens.Primary?.WorkingArea is not { } wa) return;

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
            BeginMoveDrag(e);   
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
