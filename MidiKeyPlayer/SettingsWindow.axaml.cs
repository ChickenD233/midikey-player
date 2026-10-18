using Avalonia.Controls;
using Avalonia.Interactivity;
using MidiKeyPlayer.Engine;

namespace MidiKeyPlayer;

public partial class SettingsWindow : Window
{
    private MainWindow? _mainWindow;
    private Control? _advancedBody;

    internal void AttachAdvancedBody(Control body, MainWindow owner)
    {
        _advancedBody = body;
        _mainWindow = owner;
        AdvancedHost.Content = body;
    }

    private void FillSponsorPage()
    {
        if (TxtSponsorAfdianUrl != null) TxtSponsorAfdianUrl.Text = AutoUpdate.AfdianUrlShort;
        if (BtnSponsorAfdian != null) ToolTip.SetTip(BtnSponsorAfdian, AutoUpdate.AfdianUrl);
        if (BtnSponsorBili != null) ToolTip.SetTip(BtnSponsorBili, AutoUpdate.AuthorSpaceUrlShort);
        if (BtnSponsorGitHub != null) ToolTip.SetTip(BtnSponsorGitHub, AutoUpdate.GitHubUrlShort);
        if (BtnSponsorCopyQq != null) ToolTip.SetTip(BtnSponsorCopyQq, $"复制群号 {AutoUpdate.QqGroupNumber}");
    }

    private void OpenAfdian_Click(object? sender, RoutedEventArgs e)
        => _mainWindow?.OpenAfdianForSettings();

    private void OpenBili_Click(object? sender, RoutedEventArgs e)
        => _mainWindow?.OpenAuthorSpaceForSettings();

    private void OpenGitHub_Click(object? sender, RoutedEventArgs e)
        => _mainWindow?.OpenGitHubForSettings();

    private async void CopyQq_Click(object? sender, RoutedEventArgs e)
    {
        string qq = AutoUpdate.QqGroupNumber;
        bool ok = _mainWindow != null && await _mainWindow.CopyQqForSettingsAsync(this);
        if (TxtSponsorQqCopied != null)
            TxtSponsorQqCopied.Text = ok ? $"已复制：{qq}" : $"复制失败，群号是 {qq}";
    }

    private void Pages_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyPageSize();

    private void ApplyPageSize()
    {
        if (Pages == null) return;   
        int page = Pages.SelectedIndex;
        switch (page)
        {
            case 1:   
                MinWidth = 720; MinHeight = 560;
                Width = 900; Height = 680;
                break;
            case 2:   
                MinWidth = 560; MinHeight = 380;
                Width = 660; Height = 440;
                break;
            case 3:   
                MinWidth = 700; MinHeight = 600;
                Width = 820; Height = 820;
                break;
            default:  
                MinWidth = 560; MinHeight = 500;
                Width = 640; Height = 620;   
                break;
        }
    }

    private void CloseSettings_Click(object? sender, RoutedEventArgs e) => Close();

    internal void SelectPageForDev(int index)
    {
        if (Pages != null) Pages.SelectedIndex = index;
    }

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
