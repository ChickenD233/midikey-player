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
using MidiKeyPlayer.Persist;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
// Avalonia.Input 也有 KeyBinding（手势绑定），这里给键位方案的绑定起个别名，避免歧义
using KeyBinding = MidiKeyPlayer.Engine.KeyBinding;

namespace MidiKeyPlayer;

/// <summary>
/// 「键位设置」窗口。整窗只有三块：方案行、按键绑定、功能键。
/// 没有琴盘、没有预览图、没有图例，也没有「半音怎么处理」——半音固定按「没键就不发声」处理。
///
/// 按键绑定按行排：一行一串键帽，行尾一个「+」在这一行末尾加一个音，
/// 最后一行下面一个「+」新增一行（比上一行高一个八度）。一行内绝不换行，列多了横向滚动。
///
/// 键帽上左键整块可点：点哪里都是「按一个键」改这个键的绑定。改音高与解绑在**右键菜单**里
/// （<see cref="CapBlock_ContextRequested"/>）：右键不占左键面积，点击命中不会被切碎。
///
/// 改动立即生效：写盘（<see cref="KeymapProfile.Save"/>）、刷新 <see cref="KeymapProfile.Current"/>、
/// 再通过 <see cref="ApplyPath"/> 交回主窗（主窗自己决定记日志与刷卷帘）。
/// 键位录入只挂窗口自己的 KeyDown 与 PointerPressed：窗口一关，等待态随窗口一起消失。
/// </summary>
public sealed partial class SettingsWindow : Window, INotifyPropertyChanged
{
    /// <summary>把改动交回主窗：saved 表示刚保存的方案，message 是要记进主窗日志的中文说明。</summary>
    private readonly Action<KeymapProfile?, string> _applyPath;
    /// <summary>换方案前：先把当前速度 / 移调 / 输入档位写回旧方案名。</summary>
    private readonly Action _rememberSettings;
    /// <summary>换方案后：读新方案的速度 / 移调 / 输入档位并应用，返回展示用的说明；null = 没换方案。</summary>
    private readonly Func<KeymapProfile, string?> _applyProfileSettings;

    private KeymapProfile _keymap = KeymapProfile.Default;
    private bool _loading;              // 铺值中，忽略 *Changed

    private readonly ObservableCollection<RowVM> _rows = new();
    /// <summary>按键绑定区的内容：按八度分行，一行一串键帽。</summary>
    private readonly ObservableCollection<KeymapRowGroup> _groups = new();
    private readonly ObservableCollection<FuncRowVM> _funcs = new();
    private RowVM? _row;                // 正在等待按键的主键行
    private FuncRowVM? _func;           // 正在等待按键的功能键行
    private RowVM? _pending;            // 刚加出来、还没落地的键帽（没按键就撤掉）
    private bool _schemeUsesRows;       // 这套方案写了行号；false = 老方案，退回按物理键盘的排分组

    /// <summary>单独按一下修饰键再松开就算绑上，这个窗口的时长是 400 毫秒。</summary>
    private const int ModifierTapMs = 400;
    private readonly DispatcherTimer _modTimer;
    private string _modCandidate = "";  // 已经按下、还没松开的修饰键名（"" = 没有）

    /// <summary>自然音阶一行七个音的偏移（相对本行第一个音）。</summary>
    private static readonly int[] MajorSteps = { 0, 2, 4, 5, 7, 9, 11 };

    /// <summary>「加一行」按这个顺序给新键帽挑还没被占用的键，正好是键盘上自然音阶的三排。</summary>
    private static readonly string[][] PreferredRowKeys =
    {
        new[] { "Z", "X", "C", "V", "B", "N", "M" },
        new[] { "A", "S", "D", "F", "G", "H", "J" },
        new[] { "Q", "W", "E", "R", "T", "Y", "U" },
    };

    /// <summary>无参构造只给设计器与 XAML 加载用；实际使用走带回调的那个重载。</summary>
    public SettingsWindow() : this(KeymapProfile.Current ?? KeymapProfile.Default, null, null, null)
    {
    }

    public SettingsWindow(KeymapProfile profile,
                        Action<KeymapProfile?, string>? applyPath,
                        Action? rememberSettings,
                        Func<KeymapProfile, string?>? applyProfileSettings)
    {
        _keymap = profile;
        _applyPath = applyPath ?? ((_, _) => { });
        _rememberSettings = rememberSettings ?? (() => { });
        _applyProfileSettings = applyProfileSettings ?? (_ => null);

        _modTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ModifierTapMs) };
        _modTimer.Tick += (_, _) =>
        {
            _modTimer.Stop();
            if (_modCandidate.Length > 0)
                Say($"已经按住超过 {ModifierTapMs} 毫秒。松开 {_modCandidate} 仍然绑它；按别的键就是组合键。");
        };

        InitializeComponent();
        DataContext = this;

        // 赞助页的链接文字、按钮提示与免费声明都从 AutoUpdate 的常量填：
        // 地址只有一份，XAML 里不重复写（见 SettingsWindow.FillSponsorPage）
        FillSponsorPage();

        BindRows.ItemsSource = _groups;
        FuncRows.ItemsSource = _funcs;

        KeyDown += Window_KeyDown;
        KeyUp += Window_KeyUp;
        InitUi();
        InitSyncPage();   // 「实验功能」页：接上两个列表、恢复上次的成员名（见 SettingsWindow.Experimental.cs）
    }

    // ================= 绑定给界面的属性 =================

    /// <summary>底部常驻统计行。</summary>
    public string StatsText { get; private set; } = "";

    /// <summary>一条绑定都没有时的灰字提示。</summary>
    public bool ShowEmptyHint => _rows.Count == 0;

    /// <summary>隐藏 <see cref="AvaloniaObject.PropertyChanged"/>：这里报的是窗口自己的两个绑定属性。</summary>
    public new event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ================= 初始化 =================

    private void InitUi()
    {
        RefreshUi();
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_KEYMAP")))
            Dispatcher.UIThread.Post(ReportLayout, DispatcherPriority.Background);
        if (Environment.GetEnvironmentVariable("MIDIKEY_KEYMAP_PROBE_ROUNDTRIP") is string step
            && (step == "1" || step == "2"))
            Dispatcher.UIThread.Post(() => RoundTripProbe(step), DispatcherPriority.Background);
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MIDIKEY_KEYMAP_PROBE_MODIFIER")))
            Dispatcher.UIThread.Post(ModifierProbe);
    }

    /// <summary>
    /// 【开发用，可删】修饰键单击验证：给「升高八度」按一下 Shift 再松开，看绑成什么。
    /// 用环境变量 MIDIKEY_KEYMAP_PROBE_MODIFIER=1 打开，结果写 %TEMP%\midikey-modifier-probe.log。
    /// 走的是真的 Window_KeyDown / Window_KeyUp，不是另写一套判断。
    /// </summary>
    private void ModifierProbe()
    {
        string log = Path.Combine(Path.GetTempPath(), "midikey-modifier-probe.log");
        void W(string line)
        {
            try { File.AppendAllText(log, line + "\n"); } catch { }
        }
        try
        {
            var func = _funcs.FirstOrDefault(f => f.Tag == "up");
            if (func == null) { W("探针失败：找不到「升高八度」这一行"); return; }

            W($"== 修饰键单击探针（活动方案 = {_keymap.Name}）==");
            BeginWait(func);
            W($"1) 点「{func.Label}」键帽进等待：按钮文字 = 「{func.KeyCapText}」，等待中 = {func.Waiting}");

            var down = new KeyEventArgs { Key = Key.LeftShift, KeyModifiers = KeyModifiers.Shift };
            Window_KeyDown(this, down);
            W($"2) 按下 LeftShift：按钮文字 = 「{func.KeyCapText}」，等待中 = {func.Waiting}，"
              + $"方案 octaveUp 还是 = 「{_keymap.OctaveUp}」");

            var up = new KeyEventArgs { Key = Key.LeftShift, KeyModifiers = KeyModifiers.None };
            Window_KeyUp(this, up);
            W($"3) 松开 LeftShift：方案 octaveUp = 「{_keymap.OctaveUp}」，"
              + $"按钮文字 = 「{func.KeyCapText}」，等待中 = {func.Waiting}");

            string json = File.Exists(KeymapProfile.FilePath) ? File.ReadAllText(KeymapProfile.FilePath) : "";
            string octaveLine = json.Split('\n').FirstOrDefault(l => l.Contains("octaveUp", StringComparison.Ordinal)) ?? "(没找到)";
            W($"4) 写进 keymap.json 的那一行：{octaveLine.Trim()}");

            // 绑定区两条主路径：加一行再按一个键、点方块换绑
            int keysBefore = _keymap.Keys.Count;
            RowAddRow_Click(null, new RoutedEventArgs());
            var pending = _pending;
            W($"5) 点「+ 加一行」：绑定 {keysBefore} → {_keymap.Keys.Count} 条；"
              + $"现在的行数 = {_groups.Count}，新行键帽数 = {_groups.LastOrDefault()?.Cells.Count ?? 0}，"
              + $"新键帽 = 「{pending?.KeyCapText}」（第 {(pending == null ? -1 : _rows.IndexOf(pending))} 个），"
              + $"等待中 = {pending?.Waiting}");

            Window_KeyDown(this, new KeyEventArgs { Key = Key.Q });
            W($"6) 按 Q：绑定 {_keymap.Keys.Count} 条，键盘表最后一项 = {_keymap.Keys[^1]}；"
              + $"新键帽 = 「{pending?.KeyCapText}」，等待中 = {pending?.Waiting}");

            // 错位核对：屏上每一颗键帽的键名，必须等于它自己 Source 那条键位写在方案里的键名
            int mismatched = _rows.Count(r => !string.Equals(
                r.KeyText, DisplayKey(r.Source), StringComparison.Ordinal));
            W($"7) 键帽与方案的对应：{_rows.Count} 颗键帽，键名对不上的有 {mismatched} 颗；"
              + $"屏幕第 1 行 = 「{string.Join(" ", _groups.FirstOrDefault()?.Cells.Select(c => c.KeyText) ?? Array.Empty<string>())}」");

            // 收尾：把探针加的这条拆掉，方案回到原样（脚本还会再还原一次 keymap.json）
            RowRemove_Click(new Button { DataContext = pending }, new RoutedEventArgs());
            W($"8) 收尾：绑定 {_keymap.Keys.Count} 条，行数 {_groups.Count}");
        }
        catch (Exception ex)
        {
            W("探针失败：" + ex);
        }
    }

    /// <summary>
    /// 【开发用，可删】方案往返验证。两趟进程各跑一次：
    /// step=1 另存为一个自定义方案；step=2（新进程）看它有没有被载入、在下拉里、并能删掉。
    /// </summary>
    private void RoundTripProbe(string step)
    {
        string log = Path.Combine(Path.GetTempPath(), "midikey-scheme-probe.log");
        void W(string line)
        {
            try { File.AppendAllText(log, line + "\n"); } catch { }
        }
        const string name = "往返验证方案";
        try
        {
            string path = KeymapProfile.SchemeFilePath(name);
            W($"== 第 {step} 趟进程（启动时的活动方案 = {_keymap.Name}）==");
            W($"   下拉列表 = {string.Join(" | ", KeymapProfile.ListSchemeNames())}");

            if (step == "1")
            {
                if (!string.Equals(_keymap.Name, name, StringComparison.Ordinal))
                {
                    var copy = _keymap.Clone();
                    copy.Name = name;
                    _keymap = copy;
                    KeymapProfile.Current = _keymap;
                    _keymap.Save();
                    WriteSchemeFile(name);
                    RefreshUi();
                }
                W($"   另存为「{name}」：活动方案 = {_keymap.Name}；方案文件存在 = {File.Exists(path)}");
                W($"   下拉含它 = {KeymapProfile.ListSchemeNames().Contains(name)}");
            }
            else
            {
                bool loaded = string.Equals(_keymap.Name, name, StringComparison.Ordinal);
                W($"   重启后活动方案 = {_keymap.Name}；已按名字载入它 = {loaded}");
                W($"   下拉含它 = {KeymapProfile.ListSchemeNames().Contains(name)}"
                  + $"；方案文件存在 = {File.Exists(path)}");
                bool existed = File.Exists(path);
                if (existed) File.Delete(path);
                W($"   删除它：文件删前存在 = {existed}；删后存在 = {File.Exists(path)}"
                  + $"；下拉还含它 = {KeymapProfile.ListSchemeNames().Contains(name)}");
            }
        }
        catch (Exception ex)
        {
            W("探针失败：" + ex);
        }
    }

    /// <summary>整窗铺一次：方案行、按键绑定行、三个功能键、统计行。</summary>
    private void RefreshUi()
    {
        if (BindRows == null) return;
        _loading = true;
        try
        {
            FillSchemeCombo();
            FillFuncRows();
            ChkModifiers.IsChecked = _keymap.ModifiersEnabled;
            FuncPanel.IsEnabled = _keymap.ModifiersEnabled;
            RefreshMenuEnabled();
        }
        finally
        {
            _loading = false;
        }
        RebuildRows();
    }

    /// <summary>方案下拉：内置预设 + `schemes` 目录里的用户方案，由引擎统一列出。</summary>
    private void FillSchemeCombo()
    {
        var items = new List<string>(KeymapProfile.ListSchemeNames());
        if (items.Count == 0) items.Add(KeymapProfile.DefaultName);
        if (!string.IsNullOrWhiteSpace(_keymap.Name) && !items.Contains(_keymap.Name))
            items.Add(_keymap.Name);

        KeymapCombo.ItemsSource = items;
        int idx = items.IndexOf(_keymap.Name);
        KeymapCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void FillFuncRows()
    {
        string?[] values = { _keymap.OctaveUp, _keymap.OctaveDown, _keymap.Sharp, _keymap.Flat };
        string[] tags = { "up", "down", "sharp", "flat" };
        string[] labels = { "升高八度", "降低八度", "升半音", "降低半音" };

        _funcs.Clear();
        for (int i = 0; i < 4; i++)
        {
            string key = values[i] ?? "";
            _funcs.Add(new FuncRowVM
            {
                Tag = tags[i],
                Label = labels[i],
                Key = key,
                KeyText = DisplayKey(key),
            });
        }
    }

    /// <summary>
    /// 换皮肤之后把这一页上「代码取过的画刷」全部重取一遍。
    /// 键帽与功能键的文字色、描边色是绑在 VM 属性上的资源快照，
    /// 不像 DynamicResource 会自己跟着主题走，所以换主题要显式通知一次。
    /// </summary>
    internal void RefreshThemeBrushes()
    {
        foreach (var f in _funcs) f.RefreshThemeBrushes();
        foreach (var r in _rows) r.RefreshThemeBrushes();
    }

    // ================= 按键绑定：按行排 =================
    /// <summary>
    /// 重建绑定区：把方案里的键位按**方案自己的行号**（<see cref="KeyBinding.Row"/>）分成一行行，
    /// 每行从左到右按音高升序，行号大的显示在上面。
    /// 所以预设按方案行号排出来都整整齐齐：
    /// 21 键自然音与 21 键半音的第 1 行 = 上排 Q W E R T Y U、第 2 行 = 中排 A S D F G H J、第 3 行 = 下排 Z X C V B N M；
    /// 第五人格键位的第 1 行是高音排（Q 2 W 3 E…），第 3 行是低音排（，L . ; …）；
    /// 洛克王国手碟的第 1 行是高音 T Y U、第 2 行是中音 F G H J K、第 3 行只有低音 B；
    /// Roblox 钢琴键位的第 1 行是高音排 Z X C V B N M（D6..C7），第 4 行是低音排 1 2 3 4 5 6 7 8 9 0（C2..E3）。
    /// 一行内绝不换行（XAML 里用横向 StackPanel + 横向滚动）。
    /// 老方案（所有键的 Row 都是 0）退回「按物理键盘的排分组」。
    /// </summary>
    private void RebuildRows()
    {
        _rows.Clear();
        int n = 1;
        foreach (var kb in _keymap.Keys.Where(k => k != null && !string.IsNullOrWhiteSpace(k.Key)))
            _rows.Add(NewRow(kb, n++));

        ApplyDuplicates();
        var waiting = ActiveRow();
        if (waiting != null) waiting.Waiting = true;

        // 先算出每颗键帽属于哪一行（键帽自带 Source，所以键位表变了它跟着变）。
        bool schemeHasRows = _schemeUsesRows = SchemeHasRows();
        var pairs = _rows
            .Select(r => (Cell: r, Row: schemeHasRows ? SchemeRowOf(r) : PhysicalRowOf(r.Source?.Key)))
            .ToList();

        // 行号大的显示在上面：Row 2 在最上、Row 0 在最下。
        var groups = pairs.GroupBy(p => p.Row).OrderByDescending(g => g.Key).ToList();

        _groups.Clear();
        int displayNo = 1;
        foreach (var g in groups)
        {
            var cells = g.Select(p => p.Cell).OrderBy(c => c.Pitch).ToList();
            var group = new KeymapRowGroup
            {
                RowNumber = displayNo,
                SchemeRow = g.Key,
            };
            foreach (var c in cells)
            {
                c.Parent = group;
                group.Cells.Add(c);
            }
            // 标签按分组后的实际内容算，不看物理排
            group.Label = RowLabelOf(cells, displayNo, groups.Count);
            _groups.Add(group);
            displayNo++;
        }
        Notify(nameof(ShowEmptyHint));
        RaiseStats();
    }

    /// <summary>
    /// 这套方案有没有写行号。有一处不是 0 就算有行号；全是 0（老的自定义方案、旧 JSON 导入的方案）
    /// 返回 false，界面退回按物理键盘的排分组，不会把整首方案挤成一行。
    /// </summary>
    private bool SchemeHasRows()
        => _keymap.Keys.Any(k => k != null && k.Row != 0);

    /// <summary>键帽所在方案的行号。</summary>
    private static int SchemeRowOf(RowVM cell) => cell.Source?.Row ?? 0;

    /// <summary>
    /// 【老方案回退用】键名 → 物理键盘的排号，值越大越靠下：0 数字排、1 QWERTY 排、2 ASDF 排、
    /// 3 ZXCV 排；认不出排的（鼠标键、F 键）算 4，排在最下面。
    /// </summary>
    private static int PhysicalRowOf(string? key)
    {
        string name = KeymapProfile.CanonicalKeyName(key);
        if (name.Length != 1) return 4;          // 鼠标键、F 键这类：没有排，放最后
        char c = name[0];
        if ("1234567890-=".Contains(c)) return 0;
        if ("QWERTYUIOP[]\\".Contains(c)) return 1;
        if ("ASDFGHJKL;'".Contains(c)) return 2;
        if ("ZXCVBNM,./".Contains(c)) return 3;
        return 4;
    }

    /// <summary>一行的标题：第几行、几个键、这一行弹什么到什么。跨八度时带上八度点，例如 1(do) ~ 1˙(do)。</summary>
    private static string RowLabelOf(IReadOnlyList<RowVM> cells, int index, int total)
    {
        int lo = cells.Min(c => c.Pitch);
        int hi = cells.Max(c => c.Pitch);
        string span = $"{Music.SolfegeName(lo)} ~ {Music.SolfegeName(hi)}";
        return $"第 {index} 行（共 {total} 行）· {cells.Count} 个键 · {span}";
    }

    private RowVM NewRow(KeyBinding kb, int rowNo) => new()
    {
        Source = kb,
        RowNo = rowNo,
        Pitch = PitchOf(kb),
        KeyText = DisplayKey(kb),
    };

    private int PitchOf(KeyBinding kb) => Math.Clamp(_keymap.BaseNote + kb.Offset, 0, 127);

    /// <summary>刷新底部统计行与空列表提示。</summary>
    private void RaiseStats()
    {
        StatsText = BuildStats();
        Notify(nameof(StatsText));
        Notify(nameof(ShowEmptyHint));
    }

    // ================= 显示名 =================

    /// <summary>音高文字：简谱数字加唱名，八度用上下点。例如 1(do)、#4(fa)、1˙(do)。</summary>
    internal static string NoteLabelOf(int pitch)
        => $"{Music.SolfegeName(pitch)}({SyllableOf(pitch)})";

    /// <summary>音高的唱名。简谱带升号时用本位音的唱名，例如 #4 写 fa。</summary>
    private static string SyllableOf(int pitch) => Syllables[Music.Mod(pitch, 12)];

    /// <summary>十二个半音各自的唱名（升号与它的本位音同名）。</summary>
    private static readonly string[] Syllables =
        { "do", "do", "re", "re", "mi", "fa", "fa", "sol", "sol", "la", "la", "si" };

    /// <summary>
    /// 键名在方块上的显示。**每个键名占的宽度必须一样**：方块是固定宽度的，
    /// 一格里写出「， 逗号」这种长文字，这一行后面的方块就会整体右移，点到的格子与看到的字对不上（错位）。
    /// 逗号的说明改放进方块按钮的提示气泡（<see cref="RowVM.CapTip"/>）。
    /// </summary>
    private static string DisplayKey(string? key) => string.IsNullOrEmpty(key) ? "" : DisplayKeyText(key);

    /// <summary>
    /// 一条键位在方块上的显示名。
    /// 自带 Shift 的键位（<see cref="KeyBinding.Shift"/>）显示上位字符：1 → !、q → Q、, → &lt;。
    /// 「一个半音一条键位」的方案（<see cref="KeymapProfile.HasSelfShiftKeys"/>，Roblox 钢琴就是）
    /// 里，白键字母按游戏写谱子的习惯显示小写（q），与黑键的大写（Q）区分开；
    /// 其余方案照旧显示大写键名，一个字都不变。
    /// </summary>
    private string DisplayKey(KeyBinding? binding)
    {
        if (binding == null || string.IsNullOrEmpty(binding.Key)) return "";
        if (binding.Shift) return DisplayKeyText(KeymapProfile.ShiftedNameOf(binding.Key));

        string name = KeymapProfile.CanonicalKeyName(binding.Key);
        if (_keymap.HasSelfShiftKeys && name.Length == 1 && name[0] is >= 'A' and <= 'Z')
            return name.ToLowerInvariant();
        return DisplayKeyText(name);
    }

    private static string DisplayKeyText(string key) => key switch
    {
        "Escape" => "Esc",
        "Back" => "Backspace",
        "Space" => "Space",
        "PageUp" => "PageUp",
        "PageDown" => "PageDown",
        "MouseLeft" => "鼠标左键",
        "MouseRight" => "鼠标右键",
        "MouseMiddle" => "鼠标中键",
        _ => key,
    };

    /// <summary>键名太长时方块里塞不下，提示气泡里用它补一句人话。</summary>
    internal static string LongNameOf(string key) => key switch
    {
        "," => "，逗号",
        "." => "。句点",
        ";" => "；分号",
        "/" => "／斜杠",
        "-" => "- 减号",
        "[" => "[ 左方括号",
        "]" => "] 右方括号",
        "'" => "' 单引号",
        "\\" => "\\ 反斜杠",
        "=" => "= 等号",
        "`" => "` 反引号",
        _ => "",
    };

    private void Say(string message)
    {
        if (TxtSchemeHint != null) TxtSchemeHint.Text = message;
        _applyPath(null, message);
    }

    /// <summary>
    /// 把当前方案存盘并交回主窗（主窗负责同步设置记忆与卷帘）。
    /// **任何方案都要顺手写回 schemes\&lt;方案名&gt;.json**（内置三套也一样）：
    /// keymap.json 只装活动方案，换方案时会被下一份覆盖，不写这一份改动就永久丢失。
    /// 加载时 <see cref="KeymapProfile.LoadByName"/> 先读用户目录的同名文件，读不到才用内置预设。
    /// </summary>
    private void Apply(string message)
    {
        try { _keymap.Save(); }
        catch (Exception ex) { message += $"（方案保存失败：{ex.Message}）"; }
        if (!string.IsNullOrWhiteSpace(_keymap.Name)
            && !WriteSchemeFile(_keymap.Name, out string schemeError))
            message += $"（方案文件没写成：{schemeError}）";
        KeymapProfile.Current = _keymap;
        if (TxtSchemeHint != null) TxtSchemeHint.Text = message;
        _applyPath(_keymap, message);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        EndWaiting(false);
        // 关窗要把远程同演的会话收掉，否则窗口关了还在连代理、还在按掩码过滤声部。
        CloseSyncSession();
        OnSettingsClosed();   // 把「常规」页的内容还回主窗（见 SettingsWindow.axaml.cs）
    }

    // ================= 等待按键 =================

    private RowVM? ActiveRow() => _rows.FirstOrDefault(r => r.Waiting);
    private FuncRowVM? ActiveFuncRow() => _funcs.FirstOrDefault(f => f.Waiting);

    /// <summary>
    /// 点键帽方块：进入等待，再按一个键就把这个键绑到这个音。
    ///
    /// 定位口径：改哪一个格子，只认按钮自己的 <see cref="RowVM"/>，不看坐标、不看下标。
    /// 每颗键帽与它的 <see cref="KeyBinding"/> 是一一对应的（<see cref="RowVM.Source"/>），
    /// 所以「点哪个方块改哪个方块」由数据绑定保证，不经过任何行号 / 下标换算。
    ///
    /// 这里先 <see cref="EndWaiting"/>：它可能撤掉上一颗「+」加出来还没落键的格子，并重建列表。
    /// 重建会把 <see cref="RowVM"/> 换成新对象，所以按钮上带着的那个可能已经过时 ——
    /// 按 <see cref="RowVM.Source"/> 在活列表里找回现在代表这条键位的那一颗再进等待。
    /// </summary>
    private void BlockKey_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not RowVM row) return;
        e.Handled = true;

        var source = row.Source;
        EndWaiting(false);

        if (source == null)
        {
            // 这一格还没落键（「+」刚加出来的）：重建后它已经不在列表里
            if (!_rows.Contains(row))
            {
                Say("界面已经刷新，请再点一次。");
                return;
            }
        }
        else
        {
            var live = _rows.FirstOrDefault(r => ReferenceEquals(r.Source, source));
            if (live == null)
            {
                Say("界面已经刷新，请再点一次。");
                return;
            }
            row = live;
        }
        BeginWait(row);
    }

    /// <summary>点功能键的键帽：进入等待。</summary>
    private void FuncKey_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not FuncRowVM row) return;
        BeginWait(row);
        e.Handled = true;
    }

    private void BeginWait(RowVM row)
    {
        EndWaiting(false);
        _row = row;
        _func = null;
        row.Waiting = true;
        Say($"正在等按键：按一个键就绑到 {Music.SolfegeName(row.Pitch)}。按 Esc 取消。Shift、Ctrl、Alt 单独按一下再松开也行。");
    }

    private void BeginWait(FuncRowVM row)
    {
        EndWaiting(false);
        _row = null;
        _func = row;
        row.Waiting = true;
        Say($"正在等按键：按一个键就作为「{row.Label}」。按 Esc 取消。Shift、Ctrl、Alt 单独按一下再松开也行。");
    }

    /// <summary>退出等待。commit = true 表示这一条已经落地，不用回滚。</summary>
    private void EndWaiting(bool commit)
    {
        var row = _row;
        var func = _func;
        var pending = _pending;
        _row = null;
        _func = null;
        _pending = null;
        CancelModifierTap();

        if (row != null) row.Waiting = false;
        if (func != null) func.Waiting = false;
        // 「+」加出来、还没按任何键就点别处：这个键帽直接撤掉
        if (!commit && pending != null && ReferenceEquals(pending, row) && pending.Source == null)
        {
            _rows.Remove(pending);
            pending.Parent?.Cells.Remove(pending);
            RebuildRows();
        }
    }

    // ================= 单独按修饰键 =================

    /// <summary>
    /// 修饰键的键名。认不出返回 null。Windows 上按 Alt 会走 <see cref="Key.System"/>，
    /// 这时靠按住的修饰键来认。
    /// </summary>
    private static string? ModifierNameOf(KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) return "Shift";
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) return "Ctrl";
        if (e.Key is Key.LeftAlt or Key.RightAlt) return "Alt";
        if (e.Key == Key.System)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) return "Ctrl";
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return "Shift";
            return "Alt";
        }
        return null;
    }

    private static bool IsAnyModifierKey(Key key) =>
        key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.System;

    /// <summary>按下修饰键：先记下来，等松开。期间按了别的键就当组合键，不算绑定。</summary>
    private void ArmModifierTap(string name)
    {
        _modCandidate = name;
        _modTimer.Stop();
        _modTimer.Start();
        Say($"正在等按键：松开 {name} 就把它绑上（{ModifierTapMs} 毫秒内）。按别的键就是组合键，不会绑 {name}。");
    }

    private void CancelModifierTap()
    {
        _modCandidate = "";
        _modTimer.Stop();
    }

    /// <summary>松开修饰键：这一下如果没有夹着别的键，就把它绑上。</summary>
    private void Window_KeyUp(object? sender, KeyEventArgs e)
    {
        if (_modCandidate.Length == 0) return;
        if (ActiveRow() == null && ActiveFuncRow() == null) { CancelModifierTap(); return; }
        if (!IsAnyModifierKey(e.Key)) return;

        string name = _modCandidate;
        CancelModifierTap();
        CommitKey(name);
        e.Handled = true;
    }

    /// <summary>窗口级按键录入。只有在等待态才处理。</summary>
    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (ActiveRow() == null && ActiveFuncRow() == null) return;

        if (e.Key == Key.Escape)
        {
            EndWaiting(false);
            Say("已取消。");
            e.Handled = true;
            return;
        }

        // Shift / Ctrl / Alt：单独按一下再松开也算一个键，先记下，等松开再绑
        string? mod = ModifierNameOf(e);
        if (mod != null)
        {
            ArmModifierTap(mod);
            e.Handled = true;
            return;
        }

        // 按了别的键：刚才那个修饰键是组合键的一部分，取消掉
        CancelModifierTap();

        string? label = KeyLabelOf(e.Key);
        if (label == null) return;

        // 这个键已经被另一个功能键占用了：说清楚，不写错地方
        foreach (var f in _funcs)
        {
            if (f == _func) continue;
            if (!string.IsNullOrEmpty(f.Key) &&
                string.Equals(KeymapProfile.CanonicalKeyName(f.Key), label, StringComparison.OrdinalIgnoreCase))
            {
                Say($"这个键已经是「{f.Label}」了。请换一个键。");
                return;
            }
        }

        e.Handled = true;
        CommitKey(label);
    }

    /// <summary>
    /// 窗口级指针录入：等待中在**空白处**按鼠标键就绑上。
    ///
    /// 点在任何一个按钮里（键帽方块、功能键按钮、「✕」、「+」）都不在这里处理，交给那个按钮自己的
    /// 点击事件。这是「点哪个方块改哪个方块」的第二半：
    /// 早先这里会把鼠标键当成绑定键落下去，再触发按钮的 Click，一次点击变成两件事 ——
    /// 刚绑上的那格立刻被重建成「等待按键」，而用户以为是旁边那格变了。
    /// </summary>
    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ActiveRow() == null && ActiveFuncRow() == null) return;

        var props = e.GetCurrentPoint(this).Properties;
        string? label = props.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => "MouseLeft",
            PointerUpdateKind.RightButtonPressed => "MouseRight",
            PointerUpdateKind.MiddleButtonPressed => "MouseMiddle",
            _ => null,
        };
        if (label == null) return;

        // 鼠标一点，之前按下的修饰键就不算「单独按一下」了
        CancelModifierTap();

        // 点在按钮上：不是「按了一个鼠标键」，由那个按钮自己的 Click 处理
        if (IsInsideButton(e.Source as Visual)) return;

        CommitKey(label);
    }

    /// <summary>点在任意按钮内部（键帽方块、功能键按钮、解绑「✕」、加音「+」）时为真。</summary>
    private static bool IsInsideButton(Visual? source)
        => source != null && source.FindAncestorOfType<Button>(true) is not null;

    /// <summary>落下这个键：主键行写 Key，功能键行写 up / down / sharp。</summary>
    private void CommitKey(string label)
    {
        string canonical = KeymapProfile.CanonicalKeyName(label);
        if (canonical.Length == 0)
        {
            Say($"不认识的键名「{label}」，请换一个键。");
            return;
        }

        var func = _func;
        var row = _row;
        EndWaiting(true);

        if (func != null)
        {
            switch (func.Tag)
            {
                case "up": _keymap.OctaveUp = canonical; break;
                case "down": _keymap.OctaveDown = canonical; break;
                case "sharp": _keymap.Sharp = canonical; break;
                case "flat": _keymap.Flat = canonical; break;
            }
            func.Key = canonical;
            func.KeyText = DisplayKeyText(canonical);
            Apply($"功能键已改：「{func.Label}」= {DisplayKeyText(canonical)}");
            return;
        }

        if (row == null) return;

        // 同一个键在别的行上已经用过：按需求允许并存，只给提示，不阻止保存
        var same = _rows.Where(r => r != row && string.Equals(r.Source?.Key, canonical, StringComparison.OrdinalIgnoreCase)).ToList();

        var source = row.Source;
        if (source == null)
        {
            // 老方案整份退回物理排分组时不能只给新键写行号，否则一按就跳成「有行号」的方案。
            source = new KeyBinding
            {
                Key = canonical,
                Offset = row.Pitch - _keymap.BaseNote,
                Row = _schemeUsesRows ? row.Parent?.SchemeRow ?? 0 : 0,
            };
            _keymap.Keys.Add(source);
        }
        else
        {
            source.Key = canonical;
        }
        // RebuildRows 会按方案重造一遍 RowVM。这里按 Source 找回「现在屏上代表这条键位」的那一颗，
        // 只改它的文字。不改旧对象：旧对象已经不在界面上，改了也看不见（内容与位置就不同步了）。
        RebuildRows();
        var live = _rows.FirstOrDefault(r => ReferenceEquals(r.Source, source));
        if (live != null) live.KeyText = DisplayKey(source);
        Apply($"{Music.SolfegeName(row.Pitch)} 已绑到 {KeymapProfile.DisplayNameOf(source)}"
              + (same.Count > 0 ? "。这个键还绑在别的音上，两个音都会响。" : "。"));
    }

    // ================= 主键方块：音高 / 解绑 / 加音 / 加行 =================

    /// <summary>行尾的「+」：在这一行末尾加一个音（列）。</summary>
    private void RowAddCell_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not KeymapRowGroup group) return;
        e.Handled = true;
        EndWaiting(false);
        // EndWaiting 可能刚撤掉上一格并重建列表：那时 sender 上的 group 已经是旧对象，
        // 挂在它上面的新格子不会显示。按行号在活列表里重新取一次。
        group = _groups.FirstOrDefault(g => g.SchemeRow == group.SchemeRow) ?? group;

        int step = RowStepOf(group);
        int anchor = group.Cells.Count == 0
            ? Math.Clamp(_keymap.BaseNote, 0, 127)
            : group.Cells.Max(x => x.Pitch);
        int pitch = Math.Clamp(anchor + step, 0, 127);
        if (group.Cells.Any(x => x.Pitch == pitch))
        {
            Say($"「{group.Label}」这一行已经有 {Music.SolfegeName(pitch)} 了，换一个音高或换一行加吧。");
            return;
        }

        var row = NewPendingCell(pitch);
        row.Parent = group;
        group.Cells.Add(row);
        _rows.Add(row);
        // 这里不能先 RebuildRows()：重建会把 _groups 换成一批新对象，而且这条还没落地
        // （没有 Source、没写进 _keymap.Keys）的键帽会被整条丢掉，取行的引用也一起失效。
        // 手上的 group 就是屏上活的那一行，新音高一定大于本行所有音高，加在末尾就是最右边。
        // 落键时 RebuildRows 会把这一格排回队伍，行号取 row.Parent.SchemeRow（见 CommitKey）。
        BeginWait(row);
        _pending = row;
        Say($"在「{group.Label}」末尾加了一个音（{Music.SolfegeName(pitch)}）：现在按一个键就绑上。"
            + "没按就点别处，这一格会撤掉。");
    }

    /// <summary>最后一行下面的「+」：新增一行，音高比上一行高一个八度，行号 = 当前最大行号 + 1。</summary>
    private void RowAddRow_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        EndWaiting(false);

        // 新行的行号接在现有行号后面：行号从 0 起，所以「最大 + 1」。
        // 老方案（退回物理排分组）整份没有行号，新键也写 0，免得一加行就跳成「有行号」的方案。
        int newRow = 0;
        if (_schemeUsesRows)
        {
            int maxRow = 0;
            foreach (var k in _keymap.Keys)
                if (k != null && k.Row > maxRow) maxRow = k.Row;
            newRow = maxRow + 1;
        }

        int start = _rows.Count == 0
            ? Math.Clamp(_keymap.BaseNote, 0, 127)
            : Math.Clamp(_rows.Max(r => r.Pitch) + 12, 0, 127);

        // 新行固定按自然音阶排：do re mi fa sol la si 七个音。
        var pitches = new List<int>();
        foreach (int d in MajorSteps)
            pitches.Add(Math.Clamp(start + d, 0, 127));

        // 给这一行挑还没被占用的键，顺便把这七条写进方案：按钮上直接就能看到键名。
        var keys = NextFreeKeys(pitches.Count);
        if (keys.Count < pitches.Count)
        {
            // 可用键用尽：一条都不写。写空键名会让 keymap.json 整份读不回来（FromJson 会拒收）。
            Say($"可用键已用完，请先解绑一些键。这一行要 {pitches.Count} 个键，现在只剩 {keys.Count} 个。");
            return;
        }
        var group = new KeymapRowGroup { SchemeRow = newRow };
        for (int i = 0; i < pitches.Count; i++)
        {
            var kb = new KeyBinding
            {
                Key = keys[i],
                Offset = pitches[i] - _keymap.BaseNote,
                Row = newRow,
            };
            _keymap.Keys.Add(kb);
            var row = NewPendingCell(pitches[i]);
            row.Source = kb;
            row.KeyText = DisplayKey(kb);
            row.Parent = group;
            group.Cells.Add(row);
            _rows.Add(row);
        }

        RebuildRows();
        // 走 Apply 而不是 Say：这七条键位也属于「方案的改动」，要立刻落盘（含自定义方案的方案文件）
        Apply($"加了一行「{_groups[0].Label}」：新的一行在最上面，键名已经自动填上（{string.Join(" ", keys)}）。"
              + "点某一格的下半块可以改成别的键。");
    }

    /// <summary>
    /// 挑 count 个还没被方案占用的键：先按 ZXCV 排 → ASDF 排 → QWERTY 排的顺序，
    /// 三排都用完了再按 <see cref="KeymapProfile.SingleCharPool"/> 补（26 字母 + 10 数字 + 11 标点）。
    /// 池子里的键不够时，返回的个数**少于** count：绝不补空键名。
    /// </summary>
    private List<string> NextFreeKeys(int count)
    {
        var used = BoundKeys();
        var picked = new List<string>(count);

        bool Take(string key)
        {
            string name = KeymapProfile.CanonicalKeyName(key);
            if (name.Length == 0 || used.Contains(name)) return false;
            used.Add(name);
            picked.Add(name);
            return true;
        }

        foreach (var row in PreferredRowKeys)
        {
            if (picked.Count >= count) break;
            foreach (string k in row)
            {
                if (picked.Count >= count) break;
                Take(k);
            }
        }
        foreach (char ch in KeymapProfile.SingleCharPool)
        {
            if (picked.Count >= count) break;
            Take(ch.ToString());
        }
        // 池子用尽：少给几个，由调用方提示并中止这一次操作
        return picked;
    }

    /// <summary>新键帽：还没绑键、音高已经定好。</summary>
    private RowVM NewPendingCell(int pitch) => new()
    {
        RowNo = _rows.Count + 1,
        Pitch = pitch,
        Source = null,
    };

    /// <summary>一行里相邻两格最小的正间隔；只有一格时按 1 个半音。</summary>
    private static int RowStepOf(KeymapRowGroup group)
    {
        var ordered = group.Cells.OrderBy(x => x.Pitch).Select(x => x.Pitch).ToList();
        int step = 1;
        for (int i = 1; i < ordered.Count; i++)
        {
            int d = ordered[i] - ordered[i - 1];
            if (d > 0) { step = d; break; }
        }
        return step;
    }

    /// <summary>点右上角的「✕」：解绑。与右键菜单的「解绑这个键」走同一条。</summary>
    private void RowRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not RowVM row) return;
        e.Handled = true;
        UnbindCell(row);
    }

    /// <summary>
    /// 解绑一颗键帽：从方案里删掉这条键位、从屏上的行里摘掉这一格，然后重排。
    /// 右上角的「✕」与键帽右键菜单的「解绑这个键」共用这里。
    /// </summary>
    private void UnbindCell(RowVM row)
    {
        if (ReferenceEquals(_row, row) || ReferenceEquals(_pending, row)) EndWaiting(false);

        if (row.Source != null)
        {
            _keymap.Keys.Remove(row.Source);
            Apply($"已解绑 {Music.SolfegeName(row.Pitch)}。");
        }
        _rows.Remove(row);
        row.Parent?.Cells.Remove(row);
        RebuildRows();
    }

    // ================= 键帽右键菜单：改音高 / 解绑 =================

    /// <summary>
    /// 键帽上点右键：弹出**这一颗键帽自己**的菜单（改音高… / 解绑这个键）。
    ///
    /// 为什么用右键菜单：整块左键已经用来「按一个键」了，再从方块里切一块当音高入口，
    /// 就会回到上一轮的点击错位。右键不占左键的任何面积，方块仍然整块可点、点哪改哪。
    ///
    /// 等待按键时不弹菜单：这时的右键属于「绑鼠标右键」那条既有路径，
    /// 由窗口上的 <see cref="Window_PointerPressed"/> 说了算；这里不插一手。
    /// </summary>
    private void CapBlock_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not RowVM row) return;

        // 先吃掉这一次右键请求：ContextRequested 同时走 Tunnel 与 Bubble，
        // 不吃掉会在另一半路由上再进来一次，弹出两个叠着的菜单。
        e.Handled = true;

        if (ActiveRow() != null || ActiveFuncRow() != null) return;

        BuildCapMenu(c, row).Open(c);
    }

    /// <summary>
    /// 键帽的右键菜单。菜单每次现造，菜单项直接抓住**这颗**键帽（<see cref="RowVM"/>），
    /// 不靠下标、不靠坐标，改的一定是右键点的那一颗。
    /// </summary>
    private ContextMenu BuildCapMenu(Control target, RowVM row)
    {
        var pitch = new MenuItem { Header = "改音高…" };
        pitch.Click += (_, _) => ShowPitchPicker(target, row);

        var unbind = new MenuItem { Header = "解绑这个键" };
        unbind.Click += (_, _) => UnbindCell(row);

        var menu = new ContextMenu();
        menu.Items.Add(pitch);
        menu.Items.Add(new Separator());
        menu.Items.Add(unbind);
        return menu;
    }

    /// <summary>
    /// 音高列表：本方案实际能弹出来的音，升序，按简谱写（形如 1(do)、#4(fa)、1˙(do)）。
    /// 当前那一颗高亮并滚到可见处；列表长时可上下滚动。选中即刻改音高并落盘。
    /// </summary>
    private void ShowPitchPicker(Control target, RowVM row)
    {
        if (row.Source == null)
        {
            Say("这一格还没绑键。先按一个键，再改音高。");
            return;
        }

        var choices = PitchChoices(row.Pitch);
        var list = new ListBox
        {
            ItemsSource = choices,
            MinWidth = 130,
            MaxHeight = 260,     // 列表长时滚动，不撑出屏幕
            FontSize = 12.5,
        };
        // 先铺当前值、再挂事件：反过来的话「铺值」这一次选择会被当成用户选中，一弹开就把音高改了
        int current = choices.IndexOf(NoteLabelOf(row.Pitch));
        if (current >= 0) list.SelectedIndex = current;

        var flyout = new Flyout { Content = list, Placement = PlacementMode.Bottom };
        list.AttachedToVisualTree += (_, _) =>
        {
            if (list.SelectedIndex >= 0) list.ScrollIntoView(list.SelectedIndex);
        };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not string label) return;
            flyout.Hide();
            ApplyPitch(row, label);
        };
        // 等这一轮输入走完再弹列表：菜单项点完 ContextMenu 才关，立刻弹会被它一起收掉
        Dispatcher.UIThread.Post(() => flyout.ShowAt(target), DispatcherPriority.Background);
    }

    /// <summary>
    /// 音高列表的内容：本方案能弹到的音高，升序去重。includePitch 一定进列表 ——
    /// 这一格当前的音高即使不在「能弹集合」里（键名认不出来时），也要能看见、能选中。
    /// </summary>
    private List<string> PitchChoices(int includePitch)
    {
        var pitches = new SortedSet<int>(_keymap.ReachablePitches())
        {
            Math.Clamp(includePitch, 0, 127),
        };
        return pitches.Select(NoteLabelOf).ToList();
    }

    /// <summary>
    /// 把一颗键帽的音高改成列表里选中的那个：改写这条键位的 <see cref="KeyBinding.Offset"/>，
    /// 再重排一次。重排会一起刷新方块大字、行标签的音域、底部统计三处。
    /// </summary>
    private void ApplyPitch(RowVM row, string label)
    {
        var source = row.Source;
        if (source == null) return;

        int pitch = PitchOfLabel(label, row.Pitch);
        if (pitch == row.Pitch) return;

        source.Offset = pitch - _keymap.BaseNote;
        RebuildRows();
        Apply($"{DisplayKeyText(source.Key)} 的音高已改到 {Music.SolfegeName(pitch)}。");
    }

    /// <summary>把列表里的「1(do)」换回音高数字。认不出来就保持原值。</summary>
    private static int PitchOfLabel(string label, int fallback)
    {
        int i = label.IndexOf('(');
        string name = (i > 0 ? label[..i] : label).Trim();
        for (int p = 0; p <= 127; p++)
            if (string.Equals(Music.SolfegeNames[p], name, StringComparison.Ordinal)) return p;
        return fallback;
    }

    // ================= 功能键开关 =================

    /// <summary>
    /// 「启用功能键」勾选框。不勾时：三个功能键的输入禁用，引擎按「没有修饰键」处理 ——
    /// 超出键位范围的音直接不发声，不做八度折叠（见 <see cref="KeymapProfile.ModifiersEnabled"/>）。
    /// </summary>
    private void Modifiers_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = ChkModifiers.IsChecked == true;
        if (_keymap.ModifiersEnabled == on) return;

        EndWaiting(false);
        _keymap.ModifiersEnabled = on;
        FuncPanel.IsEnabled = on;
        RebuildRows();   // 音域跟着变，行分组与统计都要重算
        Apply(on
            ? "功能键已启用：八度键与半音键照方案里的绑定生效。"
            : "功能键已关闭：四个功能键都不生效，超出键位范围的音直接不发声。");
    }

    // ================= 功能键 =================

    private void FuncClear_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not FuncRowVM row) return;
        ClearFuncKey(row);
        e.Handled = true;
    }

    /// <summary>把一个功能键设成「未设置」并写盘。</summary>
    private void ClearFuncKey(FuncRowVM row)
    {
        if (ReferenceEquals(_func, row)) EndWaiting(false);
        switch (row.Tag)
        {
            case "up": _keymap.OctaveUp = ""; break;
            case "down": _keymap.OctaveDown = ""; break;
            case "sharp": _keymap.Sharp = ""; break;
            case "flat": _keymap.Flat = ""; break;
        }
        row.Key = "";
        row.KeyText = "";
        Apply($"「{row.Label}」已设为未绑。");
    }

    // ================= 方案管理 =================

    private void KeymapCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (KeymapCombo.SelectedItem is not string name) return;
        if (string.Equals(name, _keymap.Name, StringComparison.Ordinal)) return;

        EndWaiting(false);
        var found = KeymapProfile.LoadByName(name);
        if (found == null)
        {
            // 文件被删掉或读不动了：把下拉拉回当前方案，别让界面停在一个不存在的方案上
            Say($"读不到方案「{name}」，已保持当前方案。");
            FillSchemeCombo();
            return;
        }

        // 顺序必须「先存旧、再读新」：_rememberSettings 按主窗当前方案名（还是旧名字）记参数，
        // _applyProfileSettings 按新方案名读参数。调换会让速度 / 移调记到错的名字下。
        _rememberSettings();
        string oldName = _keymap.Name;
        _keymap = found;
        KeymapProfile.Current = _keymap;
        try { _keymap.Save(); } catch { /* 保存失败由主窗日志告知 */ }
        string? extra = _applyProfileSettings(_keymap);
        RefreshUi();
        // 换方案要连键位一起交回主窗：Say 只传消息（saved 传 null），主窗会一直留着旧方案，
        // 音域提示与音游轨道就都停在旧方案上。这里自己写提示，再把新方案交回去。
        string msg = $"已换方案：{oldName} → {_keymap.Name}（主键 {BindCount()} 个）"
                     + (string.IsNullOrEmpty(extra) ? "。" : "。" + extra);
        if (TxtSchemeHint != null) TxtSchemeHint.Text = msg;
        _applyPath(_keymap, msg);
    }

    /// <summary>
    /// 顶上「方案」那一行的「新建方案…」：以当前方案为底复制一份，先问名字再落地。
    /// 与老版的「另存为」是同一个动作，所以只留这一个入口，菜单里不再有重复项。
    /// </summary>
    private async void SchemeNew_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            EndWaiting(false);
            string? name = await PromptName("新方案叫什么名字", "新建方案", SuggestNewSchemeName());
            if (string.IsNullOrEmpty(name)) return;

            string final = SanitizeSchemeName(name);
            if (final.Length == 0)
            {
                Say("方案名不能是空的。");
                return;
            }
            if (KeymapProfile.IsBuiltInSchemeName(final))
            {
                Say($"「{final}」是内置方案名，不能覆盖。请换一个名字。");
                return;
            }
            if (KeymapProfile.SchemeNameExists(final))
            {
                Say($"已经有同名方案「{final}」，请换一个名字。");
                return;
            }

            string from = _keymap.Name;
            var copy = _keymap.Clone();
            copy.Name = final;
            _keymap = copy;
            KeymapProfile.Current = _keymap;
            _keymap.Save();           // 活动方案仍然只写 keymap.json
            WriteSchemeFile(final);   // 另存的那一份写进 schemes 目录，重启后还能选到
            RefreshUi();
            Apply($"已新建方案「{final}」（键位照抄「{from}」）。以后用「改名…」改名字。");
        }
        catch (Exception ex)
        {
            Say($"新建方案失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>新方案的默认名字：以当前名字为底加「副本」，重名就往后排数字。</summary>
    private string SuggestNewSchemeName()
    {
        string baseName = (_keymap.Name ?? "").Trim();
        if (baseName.Length == 0) baseName = "新方案";
        string first = baseName + " 副本";
        if (!KeymapProfile.SchemeNameExists(first)) return first;

        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{first} {i}";
            if (!KeymapProfile.SchemeNameExists(candidate)) return candidate;
        }
        return first;
    }

    private async void SchemeRename_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (IsBuiltIn()) { Say("内置方案不能改，可以先新建一份。"); return; }
            EndWaiting(false);
            string? name = await PromptName("改成什么名字", "重命名方案", _keymap.Name);
            if (string.IsNullOrEmpty(name) || name == _keymap.Name) return;

            string final = SanitizeSchemeName(name);
            if (final.Length == 0) { Say("方案名不能是空的。"); return; }
            if (KeymapProfile.IsBuiltInSchemeName(final))
            {
                Say($"「{final}」是内置方案名，不能占用。请换一个名字。");
                return;
            }
            if (KeymapProfile.SchemeNameExists(final))
            {
                Say($"已经有同名方案「{final}」，请换一个名字。");
                return;
            }

            string oldName = _keymap.Name;
            string oldPath = KeymapProfile.SchemeFilePath(oldName);
            _keymap.Name = final;
            KeymapProfile.Current = _keymap;
            _keymap.Save();            // 活动方案：名字换了，keymap.json 跟着变
            WriteSchemeFile(final);    // 新名字的文件
            if (!string.Equals(oldPath, KeymapProfile.SchemeFilePath(final), StringComparison.OrdinalIgnoreCase))
                TryDeleteFile(oldPath); // 旧名字的文件删掉
            RefreshUi();
            Apply($"方案已改名：{oldName} → {final}。");
        }
        catch (Exception ex)
        {
            Say($"重命名失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void SchemeDelete_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (IsBuiltIn()) { Say("内置方案不能改，可以先新建一份。"); return; }
            EndWaiting(false);
            string name = _keymap.Name;
            bool ok = await Confirm("删除方案", $"删除方案「{name}」？删了就找不回来了。", "删除", danger: true);
            if (!ok) return;

            string path = KeymapProfile.SchemeFilePath(name);
            bool fileExisted = false;
            try
            {
                fileExisted = File.Exists(path);
                if (fileExisted) File.Delete(path);
            }
            catch (Exception ex)
            {
                Say($"删除失败：{ex.Message}");
                return;
            }

            // 活动方案换成默认内置方案
            _keymap = KeymapProfile.PresetByName(KeymapProfile.DefaultName)?.Clone() ?? KeymapProfile.Default;
            KeymapProfile.Current = _keymap;
            _keymap.Save();
            RefreshUi();
            string tail = fileExisted ? "" : "（没有找到它的方案文件）";
            Apply($"已删除方案「{name}」{tail}。已切回默认方案。");
        }
        catch (Exception ex)
        {
            Say($"删除失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void SchemeRestore_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            EndWaiting(false);
            // 内置方案现在也会把改动存进 schemes 目录，所以「恢复」必须把那份额外的文件一起覆盖掉，
            // 否则重启后又会读回改过的键位。提示里就此说明一句。
            bool ok = await Confirm("恢复默认设置",
                "把当前方案的键位与功能键改回内置键位？"
                + "这个方案在 schemes 目录里存的改动会被覆盖，你的其他方案不受影响。",
                "恢复", danger: false);
            if (!ok) return;

            string keepName = _keymap.Name;
            var fresh = IsBuiltIn()
                ? KeymapProfile.PresetByName(keepName)?.Clone()
                : KeymapProfile.PresetByName(KeymapProfile.DefaultName)?.Clone();
            if (fresh == null) { Say("找不到默认键位，未做改动。"); return; }

            fresh.Name = keepName;
            _keymap = fresh;
            KeymapProfile.Current = _keymap;
            Apply($"「{keepName}」已改回内置键位（名称不变）。");
            RefreshUi();
        }
        catch (Exception ex)
        {
            Say($"恢复默认失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool IsBuiltIn() => KeymapProfile.PresetByName(_keymap.Name) != null;

    private void RefreshMenuEnabled()
    {
        bool fixedScheme = IsBuiltIn();
        BtnRenameScheme.IsEnabled = !fixedScheme;
        MiSchemeDelete.IsEnabled = !fixedScheme;
        ToolTip.SetTip(BtnRenameScheme, fixedScheme ? "内置方案不能改，可以先新建一份" : "给这套方案换个名字");
        ToolTip.SetTip(MiSchemeDelete, fixedScheme ? "内置方案不能改，可以先新建一份" : "删掉这套方案");
    }

    // ================= 导入 / 导出 =================

    private async void KeymapExport_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            EndWaiting(false);
            string safe = string.IsNullOrWhiteSpace(_keymap.Name) ? "keymap" : _keymap.Name;
            foreach (char bad in Path.GetInvalidFileNameChars()) safe = safe.Replace(bad, '_');
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出键位方案",
                SuggestedFileName = safe + ".json",
                DefaultExtension = "json",
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new("键位方案") { Patterns = new List<string> { "*.json" } }
                }
            });
            if (file == null) return;
            string? path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) { Say("导出失败：拿不到目标路径。"); return; }
            if (!_keymap.TryExportFile(path, out string error)) { Say($"导出失败：{error}"); return; }
            Say($"已导出方案：{Path.GetFileName(path)}（主键 {BindCount()} 个）。");
        }
        catch (Exception ex)
        {
            Say($"导出失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void KeymapImport_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            EndWaiting(false);
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入键位方案",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("键位方案") { Patterns = new List<string> { "*.json" } },
                    new("所有文件") { Patterns = new List<string> { "*.*" } }
                }
            });
            if (files.Count == 0) return;
            string? path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) { Say("导入失败：拿不到文件路径。"); return; }

            if (!KeymapProfile.TryImportFile(path, out var parsed, out string why))
            {
                Say($"导入失败：{why} 原来的方案没动。");
                return;
            }

            _rememberSettings();
            string oldName = _keymap.Name;
            _keymap = parsed;
            KeymapProfile.Current = _keymap;
            _keymap.Save();
            string? extra = _applyProfileSettings(_keymap);
            RefreshUi();
            string msg = $"已导入方案：{_keymap.Name}（主键 {BindCount()} 个，"
                         + $"音域 {Music.SolfegeRange(_keymap.ResolveMinNote(), _keymap.ResolveMaxNote())}）";
            if (!string.Equals(oldName, _keymap.Name, StringComparison.Ordinal))
                msg += $"。方案名已变：{oldName} → {_keymap.Name}";
            if (!string.IsNullOrEmpty(extra)) msg += "。" + extra;
            // 名字撞上内置方案时，这一份会覆盖内置方案在 schemes 目录里的用户版本（名字只有一个）
            if (KeymapProfile.IsBuiltInSchemeName(_keymap.Name))
                msg += "。注意：这是内置方案名，导入的键位会当成这套方案的改动存进 schemes 目录，"
                     + "「恢复默认设置」可以退回内置键位。要另存一份请先「新建方案…」再导入。";
            Apply(msg);   // Apply 会把任何方案都写回 schemes\<方案名>.json，切走再切回来还在
        }
        catch (Exception ex)
        {
            Say($"导入失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ================= 方案文件 =================
    //
    // 目录与文件名的规则都在引擎里（KeymapProfile.SchemeFilePath），这里只负责读写与删。
    // 活动方案永远是 keymap.json；**任何方案**（含内置三套）每次改动都由 Apply 顺手写一份
    // schemes\<方案名>.json。加载时 KeymapProfile.LoadByName 先读它，读不到才用内置预设。

    /// <summary>把方案名整理成能当文件名的样子：去掉两端空格与非法字符。</summary>
    private static string SanitizeSchemeName(string? want)
    {
        string safe = (want ?? "").Trim();
        foreach (char bad in Path.GetInvalidFileNameChars()) safe = safe.Replace(bad, '_');
        return safe.Trim();
    }

    /// <summary>把当前方案写进 schemes\&lt;名字&gt;.json。写不动就返回 false 并给出中文原因。</summary>
    private bool WriteSchemeFile(string name, out string error)
    {
        error = "";
        try
        {
            string path = KeymapProfile.SchemeFilePath(name);
            string dir = Path.GetDirectoryName(path) ?? "";
            if (dir.Length > 0) Directory.CreateDirectory(dir);
            File.WriteAllText(path, _keymap.ToJson());
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private bool WriteSchemeFile(string name)
    {
        bool ok = WriteSchemeFile(name, out string why);
        if (!ok) Say($"方案文件没写成：{why}");
        return ok;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ================= 统计 =================

    private int BindCount() =>
        _keymap.Keys.Count(k => k != null && !string.IsNullOrWhiteSpace(k.Key));

    /// <summary>
    /// 当前方案已经占用的键名集合（规范化后）。「加一行」用它挑还没被占用的键，
    /// 功能键也算占用，免得新一行撞上八度键或升半音键。
    /// </summary>
    private HashSet<string> BoundKeys()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in _keymap.Keys)
        {
            if (k == null) continue;
            string name = KeymapProfile.CanonicalKeyName(k.Key);
            if (name.Length > 0) set.Add(name);
        }
        foreach (string? m in new[] { _keymap.OctaveUp, _keymap.OctaveDown, _keymap.Sharp, _keymap.Flat })
        {
            string name = KeymapProfile.CanonicalKeyName(m);
            if (name.Length > 0) set.Add(name);
        }
        return set;
    }

    /// <summary>
    /// 本方案「能弹范围」里还没有任何键能弹出来的音数。口径与「有键才发」一致：
    /// 范围与可弹集合都按同一套展开算（键偏移 + 可用的八度档 + 可用的升半音键）。
    /// 功能键关掉时可达音不计入；打开时八度键 / 升半音键能到的音算进能弹集合，
    /// 这些音不再计入「没有绑键」——所以「21 键自然音」绑上升半音键后不会再报 21 个。
    /// </summary>
    private int MissingInRange()
    {
        var (lo, hi) = _keymap.ReachableExtent();
        var reachable = new HashSet<int>(_keymap.ReachablePitches());
        int missing = 0;
        for (int p = lo; p <= hi; p++)
            if (!reachable.Contains(p)) missing++;
        return missing;
    }

    private string BuildStats()
    {
        int lo = _keymap.ResolveMinNote();
        int hi = _keymap.ResolveMaxNote();
        string mods = _keymap.ModifiersEnabled ? "功能键开" : "功能键关";
        return $"绑了 {BindCount()} 条，{_groups.Count} 行。能弹 {Music.SolfegeName(lo)} 到 {Music.SolfegeName(hi)}，"
               + $"其中 {MissingInRange()} 个音还没有绑键。（{mods}）";
    }

    // ================= 重复绑定提示 =================

    /// <summary>同一个物理键绑到多个音：涉及的键帽都描红边，但不阻止保存。</summary>
    private void ApplyDuplicates()
    {
        // 重复的口径是「按键 + 要不要 Shift」：自带 Shift 的键位（Roblox 钢琴的黑键）
        // 与它下面那个白键用的是同一个物理键，但发出去的字符不同，不算重复。
        static string IdOf(KeyBinding? kb)
            => kb == null || string.IsNullOrEmpty(kb.Key)
                ? ""
                : (kb.Shift ? "Shift+" : "") + kb.Key;

        var count = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _rows)
        {
            string id = IdOf(r.Source);
            if (id.Length == 0) continue;
            count.TryGetValue(id, out int c);
            count[id] = c + 1;
        }
        foreach (var r in _rows)
        {
            string id = IdOf(r.Source);
            r.IsDuplicate = id.Length > 0 && count.TryGetValue(id, out int c) && c > 1;
        }
    }

    // ================= 键盘录入 =================

    /// <summary>按键 → 键名。返回 null 表示不绑定这个键。</summary>
    private static string? KeyLabelOf(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return "NumPad" + (key - Key.NumPad0);
        if (key is >= Key.F1 and <= Key.F12) return "F" + (key - Key.F1 + 1);
        return key switch
        {
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemSemicolon => ";",
            Key.OemQuestion => "/",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemQuotes => "'",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            Key.Space => "Space",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Home => "Home",
            Key.End => "End",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Tab => "Tab",
            Key.Enter => "Enter",
            Key.Back => "Back",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            _ => null
        };
    }

    // ================= 小对话框 =================

    private async Task<string?> PromptName(string title, string okText, string initial)
    {
        var box = new TextBox { Text = initial ?? "", Width = 300 };
        var ok = new Button { Content = okText, Classes = { "accent" }, Padding = new Thickness(16, 4) };
        var cancel = new Button { Content = "取消", Classes = { "secondary" }, Padding = new Thickness(16, 4) };
        var dlg = new Window
        {
            Title = title,
            Width = 350,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = this.Background,
            FontFamily = FontFamily,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap },
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

    private async Task<bool> Confirm(string title, string body, string okText, bool danger)
    {
        var ok = new Button
        {
            Content = okText,
            Classes = { "accent" },
            Padding = new Thickness(16, 4),
        };
        var cancel = new Button { Content = "取消", Classes = { "secondary" }, Padding = new Thickness(16, 4) };
        var dlg = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = this.Background,
            FontFamily = FontFamily,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, LineHeight = 20 },
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

    // ================= 快照用诊断 =================

    /// <summary>只在拍快照时打印布局尺寸，便于核对三块是否清楚、有没有溢出。</summary>
    private void ReportLayout()
    {
        try
        {
            double contentH = this.Content is Control c ? c.Bounds.Height : 0;
            double extent = MainScroll?.Extent.Height ?? 0;
            double viewport = MainScroll?.Viewport.Height ?? 0;
            double cardSum = 0;
            if (MainScroll?.Content is Control scrollBody)
                foreach (var child in scrollBody.GetVisualChildren().OfType<Control>())
                    cardSum += child.Bounds.Height;
            string line = $"[键位窗口#{GetHashCode()}] 窗口 {Bounds.Width:F0}x{Bounds.Height:F0}；内容高 {contentH:F0}；"
                          + $"滚动区 内容 {extent:F0} / 视口 {viewport:F0}；需要滚动={extent > viewport + 0.5}；"
                          + $"三块合计 {cardSum:F0}；"
                          + $"绑定 {BindCount()} 条；行 {_groups.Count} 个"
                          + $"（每行键帽 {string.Join("/", _groups.Select(g => g.Cells.Count))}）；"
                          + $"等待行={(_row == null ? "无" : _row.PitchLabel)}"
                          + $" 等待功能键={(_func?.Label ?? "无")}；"
                          + $"功能键开关={(ChkModifiers.IsChecked == true ? "开" : "关")}；"
                          + $"方案={_keymap.Name}；能弹 {Music.SolfegeRange(_keymap.ResolveMinNote(), _keymap.ResolveMaxNote())}；"
                          + $"统计「{StatsText}」";
            Console.WriteLine(line);
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "midikey-snap.log"), line + "\n");
            }
            catch { }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("keymap layout report failed: " + ex.Message);
        }
    }
}

// ================= 视觉模型 =================

/// <summary>
/// 一行「键帽」的公共部分：主键行与功能键行共用等待态的显示规则。
/// 键帽有三种状态：有值（显示中文键名）、未设置（灰字「未绑」）、等待按键（「按一个键…」＋高亮描边）。
/// 等待态的高亮由外面一层 Border 承担：它不覆盖按钮模板，静态可见，不依赖闪烁动画。
/// </summary>
internal abstract class KeyCapRow : INotifyPropertyChanged
{
    private string _keyText = "";
    private bool _waiting;

    /// <summary>按钮上平时显示的键名。空串 = 这条还没设置。</summary>
    public string KeyText { get => _keyText; set => Set(ref _keyText, value); }

    /// <summary>是否正在等用户按一个键。</summary>
    public bool Waiting { get => _waiting; set => Set(ref _waiting, value); }

    /// <summary>没设置（方案里是空字符串）。</summary>
    public bool IsEmpty => !Waiting && _keyText.Length == 0;

    /// <summary>键帽上的按键文字：等待 → 提示语，未设置 → 灰字「未绑」，有值 → 键名。</summary>
    public string KeyCapText =>
        Waiting ? "按一个键…" :
        _keyText.Length > 0 ? _keyText :
        "未绑";

    /// <summary>未设置的键帽用次要文字色，一眼能看出「这里还没绑」。</summary>
    public IBrush? KeyCapForeground => FindBrush(IsEmpty ? "BrushTextMuted" : "BrushText");

    /// <summary>
    /// 键帽的提示气泡。方块只有 76 宽，逗号 / 句点这类键名写不下全称，
    /// 全称放在这里（<see cref="SettingsWindow.LongNameOf"/>）。等待态不改提示语。
    /// </summary>
    public string CapTip
    {
        get
        {
            // 右键菜单没有可见入口，把它的存在写进提示气泡里
            const string rightMenu = "右键这个方块可以改音高，也可以解绑。";
            if (_keyText.Length == 0) return $"点这个方块，再按一个键，就把这个键绑到这个音。{rightMenu}";
            string extra = SettingsWindow.LongNameOf(_keyText);
            return extra.Length > 0
                ? $"当前绑的是 {extra}。点一下再按一个键就能换绑。{rightMenu}"
                : $"点这个方块，再按一个键，就把这个键绑到这个音。{rightMenu}";
        }
    }

    /// <summary>等待态描边加粗一档。</summary>
    public Thickness WaitBorderThickness => Waiting ? new Thickness(2) : new Thickness(0);

    /// <summary>等待态描边色。取现有主题资源，不自造配色。</summary>
    public IBrush? WaitBorderBrush => Waiting ? FindBrush("BrushAccent") : null;

    /// <summary>
    /// 按资源名取主题画刷。走 <see cref="ThemeSwitch.BrushOf"/>：颜色住在主题字典里，
    /// 不带主题变体查不到，返回 null 会让键帽文字看不见。
    /// </summary>
    protected static IBrush? FindBrush(string key) => ThemeSwitch.BrushOf(key);

    /// <summary>
    /// 换皮肤之后重新取一次画刷：颜色是主题资源，换主题要重新算，并通知绑定刷新。
    /// 派生类有别的画刷就重写它。
    /// </summary>
    public virtual void RefreshThemeBrushes()
    {
        Raise(nameof(KeyCapForeground));
        Raise(nameof(WaitBorderBrush));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>子类改自己的字段也走这里。返回 true 表示值真的变了。</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnSet(name);
        return true;
    }

    /// <summary>字段变了之后要通知哪些属性。派生类可以补充。</summary>
    protected virtual void OnSet(string? name)
    {
        Raise(name);
        if (name == nameof(Waiting) || name == nameof(KeyText))
        {
            Raise(nameof(IsEmpty));
            Raise(nameof(KeyCapText));
            Raise(nameof(KeyCapForeground));
            Raise(nameof(WaitBorderThickness));
            Raise(nameof(WaitBorderBrush));
        }
    }

    /// <summary>事件只能从这里发：声明 PropertyChanged 的类才允许触发它。</summary>
    protected void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 一颗键帽：一个音 + 一个要绑的键。默认的二十一键方案里，一排就是七个，do re mi fa sol la si。
/// </summary>
internal sealed class RowVM : KeyCapRow
{
    public KeyBinding? Source { get; set; }
    public int RowNo { get; set; }

    /// <summary>这颗键帽属于哪一行。解绑与撤销时要从行里摘掉。</summary>
    public KeymapRowGroup? Parent { get; set; }

    private int _pitch;
    /// <summary>这个键弹出的音高（MIDI 编号）。</summary>
    public int Pitch
    {
        get => _pitch;
        set
        {
            if (!Set(ref _pitch, value)) return;
            Raise(nameof(PitchName));
        }
    }

    /// <summary>方块上的大字：简谱音高，例如 1 或 1˙。数字上下的点表示八度。</summary>
    public string PitchName => Music.SolfegeName(_pitch);

    /// <summary>报告与诊断用的音高文字，形如「1(do)」。方块里不再显示唱名。</summary>
    public string PitchLabel => SettingsWindow.NoteLabelOf(_pitch);

    private bool _duplicate;
    /// <summary>同一个物理键绑到了多个音：方块描红边提醒。</summary>
    public bool IsDuplicate
    {
        get => _duplicate;
        set
        {
            if (!Set(ref _duplicate, value)) return;
            Raise(nameof(BlockBorderBrush));
            Raise(nameof(BlockBorderThickness));
        }
    }

    /// <summary>方块描边：等待按键 → 品牌绿加粗；重复绑定 → 红边；平时 → 一像素灰边。</summary>
    public IBrush? BlockBorderBrush =>
        Waiting ? FindBrush("BrushAccent")
        : IsDuplicate ? FindBrush("BrushDanger")
        : FindBrush("BrushBorderStrong");

    /// <summary>方块描边粗细。等待态加粗一档。</summary>
    public Thickness BlockBorderThickness =>
        Waiting ? new Thickness(2) : IsDuplicate ? new Thickness(1.5) : new Thickness(1);

    protected override void OnSet(string? name)
    {
        base.OnSet(name);
        if (name == nameof(Waiting))
        {
            Raise(nameof(BlockBorderBrush));
            Raise(nameof(BlockBorderThickness));
        }
    }

    /// <summary>换皮肤之后方块描边也要重取（等待态是品牌绿、重复绑定是红）。</summary>
    public override void RefreshThemeBrushes()
    {
        base.RefreshThemeBrushes();
        Raise(nameof(BlockBorderBrush));
    }
}

/// <summary>
/// 绑定区的一行：一行键帽，从左到右音高升序，行内绝不换行。
/// 行按方案自己的行号分（<see cref="KeyBinding.Row"/>），行号大的显示在上面：
/// 上排是 QWERTY 排，下排是 ZXCV 排，每行内部从左到右音高升序。
/// 行标题写第几行、几个键、音域到哪。行尾的「+」由模板固定画在右边。
/// </summary>
internal sealed class KeymapRowGroup : INotifyPropertyChanged
{
    /// <summary>这一行的键帽。行尾的「+」不在这里。</summary>
    public ObservableCollection<RowVM> Cells { get; } = new();

    /// <summary>从 1 数起，第一行（行号最大那排）在最上面。</summary>
    public int RowNumber { get; set; }

    /// <summary>
    /// 这一行的方案行号（<see cref="KeyBinding.Row"/>）：0 是最低那排，数字越大越高。
    /// 行尾的「+」加的音、行内新绑的键都写这个值。
    /// 老方案退回按物理键盘的排分组时，这里就是物理排号，只用来分组、不再显示。
    /// </summary>
    public int SchemeRow { get; set; }

    private string _label = "";
    /// <summary>行标题，例如「第 1 行（共 3 行）· 7 个键 · 1(do) ~ 7(si)」。</summary>
    public string Label { get => _label; set => Set(ref _label, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>一行功能键。绑法与键帽一样：点一下，再按一个键。</summary>
internal sealed class FuncRowVM : KeyCapRow
{
    public string Tag { get; set; } = "";
    public string Label { get; set; } = "";
    public string Key { get; set; } = "";
}