using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace ReLaunch.App;

public sealed partial class MainWindow : Window
{
    private static readonly (string Tag, Type Page)[] Pages =
    [
        ("apps", typeof(AppsPage)),
        ("phones", typeof(PhonesPage)),
        ("remote", typeof(RemoteAccessPage)),
        ("activity", typeof(ActivityPage)),
        ("settings", typeof(SettingsPage)),
    ];

    private readonly DispatcherTimer _serviceTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    public MainWindow()
    {
        InitializeComponent();
        Title = "reLAUNCH";
        AppWindow.Resize(new SizeInt32(1180, 780));

        Nav.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItemContainer?.Tag is string tag) Navigate(tag);
        };
        Nav.Loaded += (_, _) => Nav.SelectedItem = Nav.MenuItems[0];

        _serviceTimer.Tick += async (_, _) => await CheckServiceAsync();
        Activated += async (_, _) => await CheckServiceAsync();
        _serviceTimer.Start();
    }


    public void Navigate(string tag)
    {
        var page = Pages.First(p => p.Tag == tag).Page;
        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
    }

    public static IReadOnlyList<string> PageTags => Pages.Select(p => p.Tag).ToList();

    public Type? CurrentPage => ContentFrame.CurrentSourcePageType;

    private async Task CheckServiceAsync()
    {
        try
        {
            ServiceBar.IsOpen = await ServiceClient.GetStateAsync() is null;
        }
        catch (Exception)
        {
            ServiceBar.IsOpen = true;
        }
    }
}
