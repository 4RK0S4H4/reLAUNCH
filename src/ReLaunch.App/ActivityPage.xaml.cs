using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ReLaunch.Core.Pipes;

namespace ReLaunch.App;

public sealed partial class ActivityPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    public ActivityPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => { await LoadAsync(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += async (_, _) => await LoadAsync();
    }

    private static Grid Columns()
    {
        var g = new Grid { ColumnSpacing = 12, Padding = new Thickness(12, 8, 12, 8) };
        foreach (var w in new double[] { 150, 130, 1, 100, 110 })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 1 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        return g;
    }

    private static void Fill(Grid g, string[] cells, bool header)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            var t = Ui.Label(cells[i], 13, bold: header, secondary: header);
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            var rows = await ServiceClient.GetActivityAsync();
            Message.Text = rows.Count == 0 ? "Nothing has happened yet." : "";
            Rows.Children.Clear();
            var header = Columns();
            Fill(header, ["Time", "Who", "App", "Action", "Result"], header: true);
            Rows.Children.Add(header);
            foreach (var r in rows)
            {
                var g = Columns();
                g.CornerRadius = new CornerRadius(6);
                g.Background = Ui.ThemeBrush("CardBackgroundFillColorDefaultBrush");
                var who = r.DeviceId == "desktop" ? "This PC" : r.DeviceId == "debug" ? "Debug API" : r.DeviceId;
                Fill(g, [r.Time.ToLocalTime().ToString("MMM d, HH:mm:ss"), who, r.AppName, r.Action, r.Result == "ok" ? "Done" : r.Result], header: false);
                Rows.Children.Add(g);
            }
        }
        catch (Exception ex) when (ex is RpcException or IOException or TimeoutException)
        {
            Message.Text = Ui.ErrorText(ex);
        }
    }
}
