using Avalonia.Controls;

namespace MidiKeyPlayer;

public partial class DocWindow : Window
{
    public DocWindow()
    {
        InitializeComponent();
    }

    public void ShowDoc(Window owner, string title, string content)
    {
        Title = title;
        TxtDoc.Text = content;
        TxtDoc.CaretIndex = 0;   
        Show(owner);
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
