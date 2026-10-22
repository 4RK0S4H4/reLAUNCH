using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ReLaunch.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "reLAUNCH";
        Nav.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItem is NavigationViewItem item)
                Placeholder.Text = item.Content?.ToString() ?? "";
        };
    }
}
