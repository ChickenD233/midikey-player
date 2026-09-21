using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MidiKeyPlayer;

/// <summary>
/// 设置窗口：两页，一页「常规」，一页「键位」。
///
/// - 常规页：设备接入、输入兼容、三个热键、导出按键表、主界面显示开关。
///   这些控件声明在 MainWindow.axaml 的 AdvancedStash 里，打开设置时整块交给本窗口的
///   AdvancedHost，关窗再还回主窗（见 <see cref="MainWindow.OpenSettings"/>）。
///   这样控件的 x:Name 与事件处理器全留在 MainWindow.axaml.cs，引用一行不用改。
///   播放前自检本身不在这一页：它常驻主界面状态卡（PreflightRow），这里只有显示开关。
/// - 键位页：方案、按键绑定、功能键。这部分原来是一个独立的「键位设置」窗口，
///   现在直接声明在本窗口的 XAML 里，逻辑在同类的 SettingsWindow.Keymap.cs。
///   合过来以后键位录入仍然挂在窗口级的 KeyDown / KeyUp 与 PointerPressed 上，
///   行为与原来单独开窗时一致。
///
/// 窗口大小跟着页签走：常规页小、键位页大。设置即时生效，没有「应用」按钮。
/// </summary>
public partial class SettingsWindow : Window
{
    private MainWindow? _mainWindow;
    private Control? _advancedBody;

    /// <summary>把「常规」页的内容挂上去（内容原本在主窗的隐藏容器里）。</summary>
    internal void AttachAdvancedBody(Control body, MainWindow owner)
    {
        _advancedBody = body;
        _mainWindow = owner;
        AdvancedHost.Content = body;
    }

    /// <summary>页签切换：窗口大小跟着页走，别让常规页撑成一大片空白。</summary>
    private void Pages_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Pages == null) return;   // 构造早期的防御
        bool keymapPage = Pages.SelectedIndex == 1;
        MinWidth = keymapPage ? 720 : 560;
        MinHeight = keymapPage ? 560 : 520;
        Width = keymapPage ? 900 : 640;
        Height = keymapPage ? 680 : 660;   // 常规页四张卡都露出来（「关于」卡里还有免费声明与作者链接）
    }

    private void CloseSettings_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>【开发用】切到某一页：0 = 常规，1 = 键位。</summary>
    internal void SelectPageForDev(int index)
    {
        if (Pages != null) Pages.SelectedIndex = index;
    }

    /// <summary>关窗收尾（由 <c>Window_Closed</c> 调用）：把常规页的内容还回主窗。</summary>
    internal void OnSettingsClosed()
    {
        if (_advancedBody == null) return;
        AdvancedHost.Content = null;
        var body = _advancedBody;
        _advancedBody = null;
        _mainWindow?.ReturnAdvancedBody(body);
        _mainWindow = null;
    }
}
