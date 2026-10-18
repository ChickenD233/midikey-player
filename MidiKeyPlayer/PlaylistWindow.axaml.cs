using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MidiKeyPlayer.Engine;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer;

public partial class PlaylistWindow : Window
{
    private readonly List<Playlist> _lists = new();
    private Playlist? _current;

    private bool _syncing;

    private string _query = "";

    private int _pickFor = -1;

    private readonly Dictionary<string, List<MidiCandidate>> _candCache = new(StringComparer.OrdinalIgnoreCase);

    private int _dragFrom = -1;
    private Point _dragStart;
    private bool _dragging;
    private (int Row, bool After)? _dropAt;

    private const double DragThreshold = 12;

    private const int MaxRows = 400;

    public event Action<string>? EntryActivated;

    public event Action? Changed;

    public event Action<string>? ActiveChanged;

    public string PlayingPath { get; set; } = "";

    public event Action<string, IReadOnlyList<TrackRef>>? PreviewTracks;

    public event Action? PreviewEnded;

    private DispatcherTimer? _previewDeb;

    public PlaylistWindow()
    {
        InitializeComponent();
        EntryList.AddHandler(InputElement.PointerPressedEvent, EntryList_PointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        EntryList.AddHandler(InputElement.PointerMovedEvent, EntryList_PointerMoved,
            RoutingStrategies.Bubble, handledEventsToo: true);
        EntryList.AddHandler(InputElement.PointerReleasedEvent, EntryList_PointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);
        LoadAll();
    }

    public string ActiveName { get; set; } = "";

    public string ActiveNameNow => _current?.Name ?? "";

    internal void SelectByNameForDev(string name)
    {
        LoadAll();
        SelectByName(name);
    }

    internal string DragStateForDev(Point p)
    {
        var t = DropTargetAt(p);
        return $"from={_dragFrom} dragging={_dragging} row={RowAt(p)} " +
               $"落点={(t is { } v ? $"{v.Row}{(v.After ? "下" : "上")}" : "无")} count={EntryList.ItemCount}";
    }

    internal void OpenTrackPickerForDev(int index) => ToggleTrackPicker(index);

    internal void PickTrackForDev(int entryIndex, int candIndex) => ToggleTrack(entryIndex, candIndex);

    public void SelectInitial()
    {
        int idx = _lists.FindIndex(p =>
            string.Equals(p.Name, ActiveName, StringComparison.CurrentCultureIgnoreCase));
        if (idx < 0 && _lists.Count > 0) idx = 0;
        PlaylistList.SelectedIndex = idx;
        if (idx < 0)
        {
            _current = null;
            SyncEntryList();
            Say("还没有歌单。点「新建」建一份，再用「加入 MIDI…」放歌。");
        }
    }

    private void LoadAll()
    {
        _lists.Clear();
        _lists.AddRange(PlaylistStore.LoadAll());

        _syncing = true;
        try
        {
            PlaylistList.Items.Clear();
            foreach (var p in _lists)
                PlaylistList.Items.Add(BuildPlaylistItem(p));
        }
        finally { _syncing = false; }
    }

    private ListBoxItem BuildPlaylistItem(Playlist p)
    {
        var item = new ListBoxItem
        {
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = p.Name, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = $"{p.Count} 首", FontSize = 12.5, Opacity = 0.7 },
                },
            },
            Tag = p,
        };
        ToolTip.SetTip(item, p.Name);
        return item;
    }

    private bool SaveCurrent(string what)
    {
        if (_current == null) return false;
        bool ok = PlaylistStore.Save(_current);
        if (!ok)
        {
            Say($"{what}失败：歌单写不进磁盘。日志里写了原因。");
            return false;
        }
        Changed?.Invoke();
        return true;
    }

    private async void PlaylistNew_Click(object? sender, RoutedEventArgs e)
    {
        string? name = await PromptNameAsync("新建歌单", "歌单名", SuggestName("新歌单"));
        if (name == null) return;

        if (FindByName(name) != null)
        {
            Say($"已经有一份叫「{name}」的歌单了，换个名字。");
            return;
        }

        var p = Playlist.Create(name);
        if (!PlaylistStore.Save(p)) { Say("新建失败：歌单写不进磁盘。"); return; }

        _lists.Add(p);
        _lists.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        LoadAll();
        SelectByName(name);
        Say($"已新建歌单「{name}」。用「加入 MIDI…」往里面放歌。");
        Changed?.Invoke();
    }

    private async void PlaylistRename_Click(object? sender, RoutedEventArgs e)
    {
        if (_current == null) { Say("先选一份歌单。"); return; }

        string oldName = _current.Name;
        string? name = await PromptNameAsync("歌单改名", "新名字", oldName);
        if (name == null || name == oldName) return;

        var other = FindByName(name);
        if (other != null && !ReferenceEquals(other, _current))
        {
            Say($"已经有一份叫「{name}」的歌单了，换个名字。");
            return;
        }

        _current.Name = name;
        if (!PlaylistStore.Rename(oldName, _current)) { Say("改名失败：歌单写不进磁盘。"); return; }

        LoadAll();
        SelectByName(name);
        Say($"歌单已改名为「{name}」。");
        Changed?.Invoke();
    }

    private async void PlaylistDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (_current == null) { Say("先选一份歌单。"); return; }

        bool ok = await ConfirmAsync(
            "删除歌单",
            $"要删除歌单「{_current.Name}」吗？里面的 {_current.Count} 条记录会一起没掉。\n" +
            "磁盘上的 MIDI 文件不动。",
            "删除");
        if (!ok) return;

        string name = _current.Name;
        if (!PlaylistStore.Delete(name)) { Say("删除失败：文件被占用或没有权限。"); return; }

        _current = null;
        LoadAll();
        SelectInitial();
        Say($"已删除歌单「{name}」。");
        Changed?.Invoke();
    }

    private void PlaylistList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (PlaylistList.SelectedItem is not ListBoxItem it || it.Tag is not Playlist p) return;

        _current = p;
        _pickFor = -1;
        SyncEntryList();
        StopPreview();
        Say($"当前歌单「{p.Name}」，{p.Count} 首。");
        ActiveChanged?.Invoke(p.Name);
    }

    private void SelectByName(string name)
    {
        for (int i = 0; i < PlaylistList.Items.Count; i++)
        {
            if (PlaylistList.Items[i] is ListBoxItem it && it.Tag is Playlist p &&
                string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase))
            {
                PlaylistList.SelectedIndex = i;
                _current = p;
                return;
            }
        }
    }

    private Playlist? FindByName(string name) =>
        _lists.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));

    private string SuggestName(string baseName)
    {
        if (FindByName(baseName) == null) return baseName;
        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{baseName} {i}";
            if (FindByName(candidate) == null) return candidate;
        }
        return baseName;
    }

    private async void AddFiles_Click(object? sender, RoutedEventArgs e)
    {
        if (_current == null) { Say("先选一份歌单。"); return; }

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要加入歌单的 MIDI 文件",
                AllowMultiple = true,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("MIDI 文件")
                    {
                        Patterns = new List<string> { "*.mid", "*.midi", "*.kar", "*.rmi" }
                    },
                    new("所有文件") { Patterns = new List<string> { "*.*" } }
                }
            });
            if (files.Count == 0) return;

            var paths = new List<string>();
            foreach (var f in files)
            {
                var p = f.TryGetLocalPath();
                if (!string.IsNullOrEmpty(p)) paths.Add(p);
            }
            AddPaths(paths);
        }
        catch (Exception ex) { Say($"打开文件对话框失败：{ex.GetType().Name}: {ex.Message}"); }
    }

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_current == null) { Say("先选一份歌单。"); return; }

        try
        {
            var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择一个文件夹，把它里面的 MIDI 加入歌单",
                AllowMultiple = false,
            });
            if (dirs.Count == 0) return;
            var dir = dirs[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { Say("读不到这个文件夹。"); return; }

            var paths = Directory.EnumerateFiles(dir)
                .Where(IsMidiFile)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (paths.Count == 0) { Say($"「{Path.GetFileName(dir)}」里没有 MIDI 文件。"); return; }

            AddPaths(paths);
        }
        catch (Exception ex) { Say($"读文件夹失败：{ex.GetType().Name}: {ex.Message}"); }
    }

    public void AddFolderPaths(string dir)
    {
        if (_current == null) { Say("先选一份歌单。"); return; }
        var paths = Directory.EnumerateFiles(dir).Where(IsMidiFile)
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).ToList();
        AddPaths(paths);
    }

    private void AddPaths(IReadOnlyList<string> paths)
    {
        if (_current == null || paths.Count == 0) return;

        var added = new List<PlaylistEntry>();
        foreach (var path in paths)
        {
            if (added.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            added.Add(new PlaylistEntry { Path = path });
        }
        if (added.Count == 0) { Say("这些文件已经在这次要加入的清单里了。"); return; }

        _current.Entries.AddRange(added);
        if (!SaveCurrent("加入曲目")) return;

        SyncEntryList();
        Say($"已加入 {added.Count} 首。默认全弹，点「全部 ▾」可只挑几行。");
    }

    private void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (_current == null) { Say("先选一份歌单。"); return; }
        int idx = SelectedEntryIndex();
        if (idx < 0) { Say("先在列表里点一行，再点「移除」。"); return; }

        string name = Path.GetFileName(_current.Entries[idx].Path);
        _current.Entries.RemoveAt(idx);
        if (!SaveCurrent("移除曲目")) return;

        SyncEntryList();
        Say($"已把「{name}」移出歌单。文件没删。");
    }

    private void Up_Click(object? sender, RoutedEventArgs e) => Nudge(-1);

    private void Down_Click(object? sender, RoutedEventArgs e) => Nudge(+1);

    private void Nudge(int delta)
    {
        if (_current == null) { Say("先选一份歌单。"); return; }
        int idx = SelectedEntryIndex();
        if (idx < 0) { Say("先在列表里点一行，再点上移或下移。"); return; }

        int to = idx + delta;
        if (!PlaylistModel.Move(_current.Entries, idx, to))
        {
            Say(delta < 0 ? "已经在最上面了。" : "已经在最下面了。");
            return;
        }
        if (!SaveCurrent("调整顺序")) return;

        SyncEntryList(selectAfter: to);
        Say($"第 {idx + 1} 首挪到了第 {to + 1} 位。");
    }

    private int RowAt(Point p)
    {
        int count = EntryList.ItemCount;
        if (count == 0) return -1;

        var first = EntryList.ContainerFromIndex(0);
        var last = EntryList.ContainerFromIndex(count - 1);
        if (first == null || last == null) return -1;

        var firstTop = first.TranslatePoint(new Point(0, 0), EntryList);
        var lastTop = last.TranslatePoint(new Point(0, 0), EntryList);
        if (firstTop == null || lastTop == null) return -1;

        if (p.Y < firstTop.Value.Y) return 0;
        if (p.Y >= lastTop.Value.Y + last.Bounds.Height) return count - 1;

        for (int i = 0; i < count; i++)
        {
            if (EntryList.ContainerFromIndex(i) is not Control c) continue;
            var top = c.TranslatePoint(new Point(0, 0), EntryList);
            if (top == null) continue;
            if (p.Y >= top.Value.Y && p.Y < top.Value.Y + c.Bounds.Height) return i;
        }
        return count - 1;
    }

    private (int Row, bool After)? DropTargetAt(Point p)
    {
        int row = RowAt(p);
        if (row < 0) return null;
        if (EntryList.ContainerFromIndex(row) is not Control c) return null;
        var top = c.TranslatePoint(new Point(0, 0), EntryList);
        if (top == null) return null;
        return (row, p.Y > top.Value.Y + c.Bounds.Height / 2);
    }

    private void ShowDropLine((int Row, bool After) target)
    {
        if (EntryList.ContainerFromIndex(target.Row) is not Control c) { HideDropLine(); return; }
        var top = c.TranslatePoint(new Point(0, 0), EntryList);
        if (top == null) { HideDropLine(); return; }

        double y = target.After ? top.Value.Y + c.Bounds.Height : top.Value.Y;
        double pad = EntryList.Bounds.Y;
        DropLine.Margin = new Thickness(0, Math.Max(0, pad + y - 1), 0, 0);
        DropLine.IsVisible = true;
    }

    private void HideDropLine() => DropLine.IsVisible = false;

    private void EntryList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragFrom = RowAt(e.GetPosition(EntryList));
        _dragStart = e.GetPosition(EntryList);
        _dragging = false;
        _dropAt = null;
    }

    private void EntryList_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragFrom < 0) return;
        if (!e.GetCurrentPoint(EntryList).Properties.IsLeftButtonPressed)
        {
            EndDrag();
            return;
        }

        var now = e.GetPosition(EntryList);
        if (!_dragging)
        {
            if (Math.Abs(now.Y - _dragStart.Y) < DragThreshold &&
                Math.Abs(now.X - _dragStart.X) < DragThreshold) return;
            _dragging = true;
        }

        _dropAt = DropTargetAt(now);
        if (_dropAt is { } t) ShowDropLine(t);
        else HideDropLine();
    }

    private void EntryList_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging && _current != null && _dragFrom >= 0)
        {
            var target = DropTargetAt(e.GetPosition(EntryList));
            if (target is { } t)
            {
                int insertAt = t.After ? t.Row + 1 : t.Row;
                int to = insertAt < _dragFrom ? insertAt : insertAt - 1;
                if (PlaylistModel.Move(_current.Entries, _dragFrom, to) && SaveCurrent("调整顺序"))
                {
                    SyncEntryList(selectAfter: to);
                    Say($"第 {_dragFrom + 1} 首挪到了第 {to + 1} 位。");
                }
            }
        }
        EndDrag();
    }

    private void EntryList_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_dragging) return;
        EndDrag();
    }

    private void EndDrag()
    {
        _dragFrom = -1;
        _dragging = false;
        _dropAt = null;
        HideDropLine();
    }

    private void PreviewChanged()
    {
        if (_current == null || _pickFor < 0 || _pickFor >= _current.Entries.Count)
        {
            StopPreview();
            return;
        }

        _previewDeb ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _previewDeb.Stop();
        _previewDeb.Tick -= PreviewDeb_Tick;
        _previewDeb.Tick += PreviewDeb_Tick;
        _previewDeb.Start();
    }

    private void PreviewDeb_Tick(object? sender, EventArgs e)
    {
        _previewDeb?.Stop();
        if (_current == null || _pickFor < 0 || _pickFor >= _current.Entries.Count)
        {
            StopPreview();
            return;
        }
        var entry = _current.Entries[_pickFor];
        PreviewTracks?.Invoke(entry.Path, entry.Tracks.ToList());
    }

    private void StopPreview()
    {
        _previewDeb?.Stop();
        PreviewEnded?.Invoke();
    }

    private static int RowIndexOf(object? source)
    {
        var v = source as Visual;
        while (v != null)
        {
            if (v is ListBoxItem item && item.Tag is int i) return i;
            v = v.GetVisualParent();
        }
        return -1;
    }

    private void ToggleTrackPicker(int index)
    {
        if (_current == null || index < 0 || index >= _current.Entries.Count) return;

        if (_pickFor == index) { _pickFor = -1; SyncEntryList(); StopPreview(); return; }

        var entry = _current.Entries[index];
        string name = Path.GetFileName(entry.Path);
        if (!File.Exists(entry.Path))
        {
            Say($"「{name}」已经不在磁盘上了，先把它移除或换一首。");
            return;
        }

        if (!_candCache.TryGetValue(entry.Path, out var cands))
        {
            try
            {
                cands = MidiLoader.Parse(entry.Path).Candidates;
            }
            catch (Exception ex)
            {
                Say($"读不动「{name}」：{ex.Message}");
                return;
            }
            _candCache[entry.Path] = cands;
        }

        if (cands.Count == 0) { Say($"「{name}」里没有任何候选音轨。"); return; }

        _pickFor = index;
        SyncEntryList(selectAfter: index);
        PreviewChanged();   
        Say($"「{name}」弹哪几行？勾上要弹的。");
    }

    private void ToggleTrack(int entryIndex, int candIndex)
    {
        if (_current == null || entryIndex < 0 || entryIndex >= _current.Entries.Count) return;
        if (!_candCache.TryGetValue(_current.Entries[entryIndex].Path, out var cands)) return;
        if (candIndex < 0 || candIndex >= cands.Count) return;

        var entry = _current.Entries[entryIndex];
        string name = Path.GetFileName(entry.Path);
        var c = cands[candIndex];
        var existing = entry.Tracks.FirstOrDefault(t => t.TrackIndex == c.TrackIndex && t.Channel == c.Channel);

        if (existing != null)
        {
            if (entry.Tracks.Count <= 1) { Say("至少留一行。想全都要，把每一行都勾上。"); return; }
            entry.Tracks.Remove(existing);
            Say($"「{name}」不再弹「{Describe(c)}」，还剩 {entry.Tracks.Count} 行。");
        }
        else
        {
            entry.Tracks.Add(new TrackRef { TrackIndex = c.TrackIndex, Channel = c.Channel, NameHint = c.Name });
            Say($"「{name}」已加上「{Describe(c)}」，共 {entry.Tracks.Count} 行。");
        }

        if (!SaveCurrent("保存音轨选择")) return;

        Dispatcher.UIThread.Post(() => SyncEntryList(selectAfter: entryIndex));
        PreviewChanged();   
    }

    private static string Describe(MidiCandidate c) =>
        $"轨道 {c.TrackIndex + 1} / 声道 {c.Channel + 1}「{c.Name}」{c.RoleTag} {c.RangeLabel} {c.NoteCount} 音";

    private Control BuildCandidateRow(int entryIndex, int candIndex, MidiCandidate c, bool isChecked)
    {
        var box = new CheckBox
        {
            IsChecked = isChecked,
            Content = new TextBlock
            {
                Text = Describe(c),
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        box.Click += (_, _) => ToggleTrack(entryIndex, candIndex);
        return box;
    }

    private void Search_Changed(object? sender, TextChangedEventArgs e)
    {
        string text = TxtSearch.Text ?? "";
        if (string.Equals(text, _query, StringComparison.Ordinal)) return;
        _query = text;
        SyncEntryList();
    }

    private void SyncEntryList(int? selectAfter = null)
    {
        int keep = selectAfter ?? SelectedEntryIndex();

        _syncing = true;
        try
        {
            EntryList.SelectedItem = null;
            EntryList.Items.Clear();

            if (_current == null)
            {
                TxtListTitle.Text = "曲目";
                TxtListCount.Text = "0 首";
                UpdateButtons();
                return;
            }

            TxtListTitle.Text = _current.Name;

            string q = _query.Trim();
            var shown = new List<int>();
            for (int i = 0; i < _current.Entries.Count; i++)
            {
                string file = Path.GetFileName(_current.Entries[i].Path);
                if (q.Length == 0 || file.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                    shown.Add(i);
            }

            int missing = _current.Entries.Count(x => !File.Exists(x.Path));
            TxtListCount.Text = q.Length == 0
                ? (missing > 0 ? $"{_current.Count} 首，其中 {missing} 首文件已不在" : $"{_current.Count} 首")
                : $"{shown.Count} / {_current.Count} 首";

            int cap = Math.Min(shown.Count, MaxRows);
            for (int k = 0; k < cap; k++)
                EntryList.Items.Add(BuildEntryRow(shown[k], _current.Entries[shown[k]]));

            if (shown.Count > cap)
            {
                EntryList.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock
                    {
                        Text = $"（还有 {shown.Count - cap} 首没列出来，用上面的搜索框缩小范围）",
                        FontSize = 12.5,
                        Opacity = 0.7,
                    },
                    IsEnabled = false,
                });
            }

            if (keep >= 0)
            {
                int pos = shown.IndexOf(keep);
                if (pos >= 0 && pos < cap) EntryList.SelectedIndex = pos;
            }
            UpdateButtons();
        }
        finally { _syncing = false; }
    }

    private ListBoxItem BuildEntryRow(int entryIndex, PlaylistEntry entry)
    {
        string file = Path.GetFileName(entry.Path);
        string dir = Path.GetDirectoryName(entry.Path) ?? "";
        bool missing = !File.Exists(entry.Path);
        bool playing = !string.IsNullOrEmpty(PlayingPath) &&
                       string.Equals(PlayingPath, entry.Path, StringComparison.OrdinalIgnoreCase);

        string sub = missing
            ? "文件已不在：连播时会跳过这一首"
            : entry.Tracks.Count == 0
                ? "弹：全部音轨（还没勾过）"
                : entry.Tracks.Count == 1
                    ? (entry.Tracks[0].NameHint.Length > 0
                        ? $"弹这一行的「{entry.Tracks[0].NameHint}」"
                        : $"弹：{entry.Tracks[0].Label}")
                    : $"弹 {entry.Tracks.Count} 行（合奏）";

        string trackText = entry.Tracks.Count == 0 ? "全部 ▾" : $"共 {entry.Tracks.Count} 行 ▾";

        var track = new Button
        {
            Name = "BtnTrack",
            Content = trackText,
            Classes = { "secondary" },
            Padding = new Thickness(10, 2),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !missing,
        };
        ToolTip.SetTip(track, "勾这一首弹哪几行，可以勾多个");
        track.Click += (_, _) =>
        {
            int i = RowIndexOf(track);
            if (i < 0) return;
            EntryList.SelectedIndex = i;
            ToggleTrackPicker(i);
        };

        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(new TextBlock
        {
            Text = (playing ? "▶ " : "") + file,
            FontSize = 13.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        left.Children.Add(new TextBlock
        {
            Text = sub,
            FontSize = 12.5,
            Opacity = 0.7,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*,Auto"), MinHeight = 30 };
        var grip = new TextBlock
        {
            Text = "⋮⋮",
            FontSize = 12.5,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(grip, 0);
        Grid.SetColumn(left, 1);
        Grid.SetColumn(track, 2);
        grid.Children.Add(grip);
        grid.Children.Add(left);
        grid.Children.Add(track);

        Control content = grid;

        if (_pickFor == entryIndex && _candCache.TryGetValue(entry.Path, out var cands))
        {
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 6, 0, 6),
                Background = ThemeSwitch.BrushOf("BrushBorder"),
            });
            panel.Children.Add(new TextBlock
            {
                Text = entry.Tracks.Count == 0
                    ? $"弹哪几行？现在没勾过，这一首全部 {cands.Count} 行都弹"
                    : $"弹哪几行？已勾 {entry.Tracks.Count} / {cands.Count} 行",
                FontSize = 12.5,
                Opacity = 0.7,
                Margin = new Thickness(0, 0, 0, 2),
            });

            for (int k = 0; k < cands.Count; k++)
            {
                var c = cands[k];
                bool isChecked = entry.Tracks.Any(t => t.TrackIndex == c.TrackIndex && t.Channel == c.Channel);
                panel.Children.Add(BuildCandidateRow(entryIndex, k, c, isChecked));
            }

            var box = new StackPanel();
            box.Children.Add(grid);
            box.Children.Add(panel);
            content = box;
        }

        var item = new ListBoxItem
        {
            Content = content,
            Tag = entryIndex,
            Opacity = missing ? 0.55 : 1.0,
        };
        ToolTip.SetTip(item, entry.Path + (dir.Length > 0 ? $"\n{dir}" : ""));
        item.DoubleTapped += (_, _) =>
        {
            if (!missing) EntryActivated?.Invoke(entry.Path);
        };
        return item;
    }

    private void UpdateButtons()
    {
        bool has = _current != null;
        int idx = SelectedEntryIndex();
        BtnAddFiles.IsEnabled = has;
        BtnAddFolder.IsEnabled = has;
        BtnPlaylistRename.IsEnabled = has;
        BtnPlaylistDelete.IsEnabled = has;
        BtnRemove.IsEnabled = has && idx >= 0;
        BtnUp.IsEnabled = has && idx > 0;
        BtnDown.IsEnabled = has && idx >= 0 && _current != null && idx < _current.Count - 1;
    }

    private int SelectedEntryIndex()
    {
        if (EntryList.SelectedItem is ListBoxItem it && it.Tag is int i) return i;
        return -1;
    }

    public void RefreshPlayingMark()
    {
        SyncEntryList();
    }

    private void Say(string text) => TxtStatus.Text = text;

    private static bool IsMidiFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mid" or ".midi" or ".kar" or ".rmi";
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        StopPreview();
        base.OnClosed(e);
    }

    private async Task<string?> PromptNameAsync(string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, Width = 260 };
        var ok = new Button { Content = "确定", Classes = { "accent" }, Padding = new Thickness(18, 6) };
        var cancel = new Button { Content = "取消", Classes = { "secondary" }, Padding = new Thickness(18, 6) };

        var dlg = new Window
        {
            Title = title,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            FontFamily = FontFamily,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, ok },
                    },
                },
            },
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            dlg.Close(box.Text ?? "");
            e.Handled = true;
        };
        ok.Click += (_, _) => dlg.Close(box.Text ?? "");
        cancel.Click += (_, _) => dlg.Close(null);

        var result = await dlg.ShowDialog<string?>(this);
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    private async Task<bool> ConfirmAsync(string title, string body, string okText)
    {
        var ok = new Button { Content = okText, Classes = { "accent" }, Padding = new Thickness(18, 6) };
        var cancel = new Button { Content = "取消", Classes = { "secondary" }, Padding = new Thickness(18, 6) };

        var dlg = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            FontFamily = FontFamily,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, ok },
                    },
                },
            },
        };
        ok.Click += (_, _) => dlg.Close(true);
        cancel.Click += (_, _) => dlg.Close(false);
        return await dlg.ShowDialog<bool>(this);
    }
}
