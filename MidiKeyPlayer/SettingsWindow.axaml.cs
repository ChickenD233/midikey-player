using Avalonia.Controls;
using Avalonia.Interactivity;
using MidiKeyPlayer.Engine;

namespace MidiKeyPlayer;

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

    /// <summary>赞助页：爱发电在最上面，下面是 B 站、GitHub 与 QQ 群。地址都取自 AutoUpdate。</summary>
    private void FillSponsorPage()
    {
        if (TxtSponsorAfdianUrl != null) TxtSponsorAfdianUrl.Text = AutoUpdate.AfdianUrlShort;
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

    /// <summary>页签对应的窗口尺寸。0 常规 / 1 键位 / 2 赞助 / 3 实验功能。</summary>
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
            case 2:   // 赞助页：内容短，窗口压到刚好装下，不留一大片空白
                MinWidth = 560; MinHeight = 380;
                Width = 660; Height = 440;
                break;
            case 3:   // 实验功能页：房间状态加成员表加声部表加演出控制，比赞助页长
                MinWidth = 700; MinHeight = 600;
                Width = 820; Height = 820;
                break;
            default:  // 常规页
                MinWidth = 560; MinHeight = 500;
                Width = 640; Height = 620;   // 四张卡都露出来（文字缩短后 620 就够）
                break;
        }
    }

    private void CloseSettings_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>【开发用】切到某一页：0 = 常规，1 = 键位，2 = 赞助，3 = 实验功能。</summary>
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
