using Avalonia.Controls;
using Avalonia.Interactivity;
using MidiKeyPlayer.Engine;

namespace MidiKeyPlayer;

/// <summary>
/// 设置窗口：三页，「常规」「键位」「赞助」。
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
/// - 赞助页：爱发电排在最上面，下面列作者的 B 站、GitHub 与 QQ 群。这一页没有任何设置项，
///   文案与地址全从 <see cref="AutoUpdate"/> 的常量来（见 <see cref="FillSponsorPage"/>）。
///   「程序完全免费」这类声明不重复放在这里：它留在「常规」页的关于卡里。
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

    /// <summary>赞助页：爱发电在最上面，下面是 B 站、GitHub 与 QQ 群。地址与群号都取自 AutoUpdate。</summary>
    private void FillSponsorPage()
    {
        if (TxtSponsorAfdianUrl != null) TxtSponsorAfdianUrl.Text = AutoUpdate.AfdianUrlShort;
        if (TxtSponsorBiliUrl != null) TxtSponsorBiliUrl.Text = AutoUpdate.AuthorSpaceUrlShort;
        if (TxtSponsorGitHubUrl != null) TxtSponsorGitHubUrl.Text = AutoUpdate.GitHubUrlShort;
        if (TxtSponsorQq != null) TxtSponsorQq.Text = AutoUpdate.QqGroupNumber;
        if (BtnSponsorAfdian != null) ToolTip.SetTip(BtnSponsorAfdian, AutoUpdate.AfdianUrl);
        if (BtnSponsorBili != null) ToolTip.SetTip(BtnSponsorBili, AutoUpdate.AuthorSpaceUrlShort);
        if (BtnSponsorGitHub != null) ToolTip.SetTip(BtnSponsorGitHub, AutoUpdate.GitHubUrlShort);
        if (BtnSponsorCopyQq != null) ToolTip.SetTip(BtnSponsorCopyQq, $"复制群号 {AutoUpdate.QqGroupNumber}");
    }

    /// <summary>赞助页的爱发电按钮。打开与记录日志都走主窗那条链路，行为与其它入口一致。</summary>
    private void OpenAfdian_Click(object? sender, RoutedEventArgs e)
        => _mainWindow?.OpenAfdianForSettings();

    /// <summary>赞助页的 B 站按钮。</summary>
    private void OpenBili_Click(object? sender, RoutedEventArgs e)
        => _mainWindow?.OpenAuthorSpaceForSettings();

    /// <summary>赞助页的 GitHub 按钮。</summary>
    private void OpenGitHub_Click(object? sender, RoutedEventArgs e)
        => _mainWindow?.OpenGitHubForSettings();

    /// <summary>赞助页的复制群号按钮：复制走主窗那一份实现，这里只显示结果。</summary>
    private async void CopyQq_Click(object? sender, RoutedEventArgs e)
    {
        string qq = AutoUpdate.QqGroupNumber;
        bool ok = _mainWindow != null && await _mainWindow.CopyQqForSettingsAsync(this);
        if (TxtSponsorQqCopied != null)
            TxtSponsorQqCopied.Text = ok ? $"已复制：{qq}" : $"复制失败，群号是 {qq}";
    }

    /// <summary>页签切换：窗口大小跟着页走，别让常规页撑成一大片空白。</summary>
    private void Pages_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyPageSize();

    /// <summary>页签对应的窗口尺寸。0 常规 / 1 键位 / 2 赞助。</summary>
    private void ApplyPageSize()
    {
        if (Pages == null) return;   // 构造早期的防御
        int page = Pages.SelectedIndex;
        switch (page)
        {
            case 1:   // 键位页：最大，键帽一行一行排
                MinWidth = 720; MinHeight = 560;
                Width = 900; Height = 680;
                break;
            case 2:   // 赞助页：内容不多，跟常规页差不多高就够
                MinWidth = 560; MinHeight = 520;
                Width = 660; Height = 620;
                break;
            default:  // 常规页
                MinWidth = 560; MinHeight = 520;
                Width = 640; Height = 660;   // 四张卡都露出来（「关于」卡里还有免费声明与作者链接）
                break;
        }
    }

    private void CloseSettings_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>【开发用】切到某一页：0 = 常规，1 = 键位，2 = 赞助。</summary>
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
