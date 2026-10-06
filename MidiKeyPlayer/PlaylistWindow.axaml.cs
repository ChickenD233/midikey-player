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

/// <summary>
/// 歌单窗口：建歌单、往里放曲目、替每一首预先指定音轨、改顺序。
///
/// 三个出口给主窗口：<see cref="EntryActivated"/>（双击一行，载入这一首）、
/// <see cref="Changed"/>（歌单存过盘）、<see cref="ActiveChanged"/>（换了一份当前歌单）。
///
/// 顺序规则全在 <see cref="PlaylistModel"/> 的纯函数里，上移、下移、拖放调的是同一个，
/// 所以内置自检测到的就是这里真正跑的那段代码。
/// </summary>
public partial class PlaylistWindow : Window
{
    private readonly List<Playlist> _lists = new();
    private Playlist? _current;

    /// <summary>正在铺界面。铺的过程里会改选中项，那几次不是用户点的。</summary>
    private bool _syncing;

    private string _query = "";

    /// <summary>正展开行内音轨清单的曲目下标；-1 = 没有展开的。</summary>
    private int _pickFor = -1;

    /// <summary>解析过的候选行，按文件路径缓存。展开音轨清单时不必再读一遍文件。</summary>
    private readonly Dictionary<string, List<MidiCandidate>> _candCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>拖动状态：按下时的行下标、起点、是否已经越过阈值。</summary>
    private int _dragFrom = -1;
    private Point _dragStart;
    private bool _dragging;

    /// <summary>拖动阈值（像素）。太小会和「点一下选中」打架。</summary>
    private const double DragThreshold = 12;

    /// <summary>曲目列表的行数上限。超过就只显示前 N 行，歌单上千首时界面不至于卡住。</summary>
    private const int MaxRows = 400;

    /// <summary>双击某一行：把这一首交回主窗口载入。</summary>
    public event Action<string>? EntryActivated;

    /// <summary>歌单存过盘。主窗口据此刷新左栏那张卡。</summary>
    public event Action? Changed;

    /// <summary>用户在左栏选了一份歌单。主窗口据此记成「当前歌单」，下次启动才回得来。</summary>
    public event Action<string>? ActiveChanged;

    /// <summary>主窗口正在播放的那首曲子的路径，用来在列表里打标记。</summary>
    public string PlayingPath { get; set; } = "";

    /// <summary>加入曲目时用来推荐音轨的声部身份（主窗口当前演奏行的身份）。</summary>
    public TrackRole? SuggestRole { get; set; }

    public PlaylistWindow()
    {
        InitializeComponent();
        LoadAll();
    }

    /// <summary>打开后默认选中的歌单名。窗口构造完、Show 之前设。</summary>
    public string ActiveName { get; set; } = "";

    /// <summary>窗口里当前选中的歌单名；一份都没选是空串。关窗时主窗口拿它收尾。</summary>
    public string ActiveNameNow => _current?.Name ?? "";

    /// <summary>【开发用】替快照展开第 <paramref name="index"/> 行的音轨清单，拍图才拍得到这一态。</summary>
    internal void OpenTrackPickerForDev(int index) => ToggleTrackPicker(index);

    /// <summary>【开发用】替快照选一发候选，走的是用户点候选那一行同一条代码路径。</summary>
    internal void PickTrackForDev(int entryIndex, int candIndex) => PickTrack(entryIndex, candIndex);

    /// <summary>构造之后调一次：按 <see cref="ActiveName"/> 选中对应的歌单。</summary>
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

    // ================= 载入与保存 =================

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

    /// <summary>把当前歌单存盘；没有当前歌单时什么都不做。</summary>
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

    // ================= 歌单：新建 / 改名 / 删除 =================

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
        Say($"当前歌单「{p.Name}」，{p.Count} 首。");
        ActiveChanged?.Invoke(p.Name);
    }

    /// <summary>按名字选中左侧歌单，并把它当成当前歌单。</summary>
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

    // ================= 曲目：加入 / 移除 =================

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

    /// <summary>把一个文件夹里的 MIDI 按文件名顺序整批加入歌单。</summary>
    public void AddFolderPaths(string dir)
    {
        if (_current == null) { Say("先选一份歌单。"); return; }
        var paths = Directory.EnumerateFiles(dir).Where(IsMidiFile)
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).ToList();
        AddPaths(paths);
    }

    /// <summary>
    /// 批量加入。同一首歌在歌单里出现两次是可以的，所以只拦这一次操作里重复的路径，
    /// 不拦手动重复加。
    /// </summary>
    private void AddPaths(IReadOnlyList<string> paths)
    {
        if (_current == null || paths.Count == 0) return;

        var added = new List<PlaylistEntry>();
        foreach (var path in paths)
        {
            if (added.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            added.Add(new PlaylistEntry { Path = path, Track = SuggestTrackFor(path) });
        }
        if (added.Count == 0) { Say("这些文件已经在这次要加入的清单里了。"); return; }

        _current.Entries.AddRange(added);
        if (!SaveCurrent("加入曲目")) return;

        SyncEntryList();
        Say($"已加入 {added.Count} 首。点曲目右边的「自动 ▾」可以指定弹哪一行。");
    }

    /// <summary>加入时按上一首演奏行的声部推荐一行。读不动这个文件就留空，连播时主窗口再挑一次。</summary>
    private TrackRef? SuggestTrackFor(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var parsed = MidiLoader.Parse(path);
            var pick = PlaylistModel.Pick(parsed.Candidates, null, SuggestRole);
            if (pick.Candidate is not { } c) return null;
            return new TrackRef { TrackIndex = c.TrackIndex, Channel = c.Channel, NameHint = c.Name };
        }
        catch { return null; }
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
        Say($"已把「{name}」移出歌单。磁盘上的文件没动。");
    }

    private void Up_Click(object? sender, RoutedEventArgs e) => Nudge(-1);

    private void Down_Click(object? sender, RoutedEventArgs e) => Nudge(+1);

    /// <summary>上移 / 下移一格。逻辑走 PlaylistModel，和拖放共用。</summary>
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

    // ================= 拖动改顺序 =================

    private void EntryList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragFrom = IndexAt(e.GetPosition(EntryList));
        _dragStart = e.GetPosition(EntryList);
        _dragging = false;

        // 行里的「音轨」按钮：选中这一行，然后就地展开候选清单
        if (ClickedButton(e) is { Name: "BtnTrack" })
        {
            if (_dragFrom >= 0) EntryList.SelectedIndex = _dragFrom;
            e.Handled = true;
            ToggleTrackPicker(_dragFrom);
        }
    }

    private void EntryList_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragFrom < 0) return;
        if (!e.GetCurrentPoint(EntryList).Properties.IsLeftButtonPressed)
        {
            ResetDrag();
            return;
        }

        var now = e.GetPosition(EntryList);
        if (!_dragging)
        {
            if (Math.Abs(now.Y - _dragStart.Y) < DragThreshold &&
                Math.Abs(now.X - _dragStart.X) < DragThreshold) return;
            _dragging = true;
        }
        // 拖动中：只更新悬停高亮，落点由松开时算
        int hover = IndexAt(now);
        if (hover >= 0 && hover != EntryList.SelectedIndex) EntryList.SelectedIndex = hover;
    }

    private void EntryList_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging && _current != null && _dragFrom >= 0)
        {
            int target = IndexAt(e.GetPosition(EntryList));
            if (target >= 0 && target != _dragFrom &&
                PlaylistModel.MoveOnto(_current.Entries, _dragFrom, target))
            {
                if (SaveCurrent("调整顺序"))
                {
                    int landed = _dragFrom < target ? target - 1 : target;
                    SyncEntryList(selectAfter: landed);
                    Say($"第 {_dragFrom + 1} 首挪到了第 {landed + 1} 位。");
                }
            }
        }
        ResetDrag();
    }

    /// <summary>
    /// 指针捕获丢了（拖出窗口、被别的控件抢走）。这一条只清状态，不当成落点 ——
    /// 与 PointerReleased 分开写是因为两者的事件参数类型不同，合并会被 XAML 编译器拒绝。
    /// </summary>
    private void EntryList_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => ResetDrag();

    private void ResetDrag()
    {
        _dragFrom = -1;
        _dragging = false;
    }

    /// <summary>指针落在第几行；不在任何一行上返回 -1。</summary>
    private int IndexAt(Point p)
    {
        for (int i = 0; i < EntryList.ItemCount; i++)
        {
            if (EntryList.ContainerFromIndex(i) is not Control c) continue;
            var top = c.TranslatePoint(new Point(0, 0), EntryList);
            if (top == null) continue;
            if (p.Y >= top.Value.Y && p.Y < top.Value.Y + c.Bounds.Height) return i;
        }
        return -1;
    }

    /// <summary>这次按下是不是落在某个按钮上。行里的按钮不是 InputElement 时返回 null。</summary>
    private static Button? ClickedButton(PointerPressedEventArgs e)
    {
        var src = e.Source as Visual;
        while (src != null)
        {
            if (src is Button b) return b;
            src = src.GetVisualParent();
        }
        return null;
    }

    // ================= 预先选音轨 =================

    /// <summary>
    /// 点某一行的「音轨」按钮：就地展开这一行的候选清单，再点一次收起。
    /// 候选按文件路径缓存，同一首展开第二次不再读文件。
    /// </summary>
    private void ToggleTrackPicker(int index)
    {
        if (_current == null || index < 0 || index >= _current.Entries.Count) return;

        if (_pickFor == index) { _pickFor = -1; SyncEntryList(); return; }

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
        Say($"「{name}」弹哪一行？点下面那一行即可。");
    }

    /// <summary>点了候选清单里的一行：钉住这一行。<paramref name="candIndex"/> 为 -1 = 不预设。</summary>
    private void PickTrack(int entryIndex, int candIndex)
    {
        if (_current == null || entryIndex < 0 || entryIndex >= _current.Entries.Count) return;

        var entry = _current.Entries[entryIndex];
        string name = Path.GetFileName(entry.Path);

        if (candIndex < 0)
        {
            entry.Track = null;
            Say($"「{name}」改成不预设音轨，连播时按声部自动挑。");
        }
        else if (_candCache.TryGetValue(entry.Path, out var cands) && candIndex < cands.Count)
        {
            var c = cands[candIndex];
            entry.Track = new TrackRef { TrackIndex = c.TrackIndex, Channel = c.Channel, NameHint = c.Name };
            Say($"「{name}」已设为弹「{Describe(c)}」。");
        }
        else return;

        if (!SaveCurrent("保存音轨选择")) return;

        // 重铺会把这一行连同单选钮一起换掉，也就是在单选钮自己的事件里动它，
        // 所以放到下一个循环再铺。
        _pickFor = -1;
        Dispatcher.UIThread.Post(() => SyncEntryList(selectAfter: entryIndex));
    }

    /// <summary>候选行上的说明：轨号 / 声道 / 声部 / 音域 / 音数。</summary>
    private static string Describe(MidiCandidate c) =>
        $"轨道 {c.TrackIndex + 1} / 声道 {c.Channel + 1}「{c.Name}」{c.RoleTag} {c.RangeLabel} {c.NoteCount} 音";

    /// <summary>一行候选：单选钮 + 说明。点整行都能选。</summary>
    private Control BuildCandidateRow(int entryIndex, int candIndex, MidiCandidate? c, bool selected)
    {
        var radio = new RadioButton
        {
            GroupName = "TrackPick" + entryIndex,
            IsChecked = selected,
            Content = new TextBlock
            {
                Text = c == null ? "不预设（连播时按声部自动挑）" : Describe(c),
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        radio.Click += (_, _) => PickTrack(entryIndex, candIndex);
        return radio;
    }

    // ================= 铺曲目列表 =================

    private void Search_Changed(object? sender, TextChangedEventArgs e)
    {
        string text = TxtSearch.Text ?? "";
        if (string.Equals(text, _query, StringComparison.Ordinal)) return;
        _query = text;
        SyncEntryList();
    }

    /// <summary>
    /// 按当前歌单与搜索词重铺右侧列表。
    /// 先清选中再清条目：选中项还在时清空，ListBox 会去读旧下标并抛越界。
    /// </summary>
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
            : entry.Track is { IsSet: true } t
                ? (t.NameHint.Length > 0 ? $"弹这一行的「{t.NameHint}」" : "弹指定的一行")
                : "弹哪一行：自动（按声部挑）";

        // 按钮上直接写当前选的是哪一行，不用展开就能看见
        var trackRef = entry.Track;
        string trackText = trackRef is { IsSet: true }
            ? $"轨道 {trackRef.TrackIndex + 1} ▾"
            : "自动 ▾";

        var track = new Button
        {
            Name = "BtnTrack",
            Content = trackText,
            Classes = { "secondary" },
            Padding = new Thickness(10, 2),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !missing,
        };
        ToolTip.SetTip(track, "指定这一首弹哪一行");

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

        // 这一行正展开着音轨清单：把候选直接铺在行下面，点一行就选完
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
                Text = "弹哪一行？",
                FontSize = 12.5,
                Opacity = 0.7,
                Margin = new Thickness(0, 0, 0, 2),
            });

            bool matched = false;
            for (int k = 0; k < cands.Count; k++)
            {
                var c = cands[k];
                bool isCurrent = entry.Track is { IsSet: true } t3 &&
                                 c.TrackIndex == t3.TrackIndex && c.Channel == t3.Channel;
                if (isCurrent) matched = true;
                panel.Children.Add(BuildCandidateRow(entryIndex, k, c, isCurrent));
            }
            panel.Children.Add(BuildCandidateRow(entryIndex, -1, null, entry.Track is not { IsSet: true } || !matched));

            var box = new StackPanel();
            box.Children.Add(grid);
            box.Children.Add(panel);
            content = box;
        }

        var item = new ListBoxItem
        {
            Content = content,
            Tag = entryIndex,
            // 文件不在的曲目：整行压暗。不写死灰色 —— 灰值在深色皮肤下会看不清。
            Opacity = missing ? 0.55 : 1.0,
        };
        // 附加属性，必须走 SetTip，不能当普通属性赋值
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

    /// <summary>当前选中行对应的歌单下标；没选中返回 -1。</summary>
    private int SelectedEntryIndex()
    {
        if (EntryList.SelectedItem is ListBoxItem it && it.Tag is int i) return i;
        return -1;
    }

    /// <summary>主窗口换歌之后刷新这一行标记。</summary>
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

    // ================= 两个小对话框 =================

    /// <summary>问一个名字。取消返回 null。</summary>
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

    /// <summary>问一句要不要。取消返回 false。</summary>
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
