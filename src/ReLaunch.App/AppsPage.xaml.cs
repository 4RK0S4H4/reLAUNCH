using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using ReLaunch.Core.Catalogue;
using ReLaunch.Core.Contracts;
using ReLaunch.Core.Pipes;

namespace ReLaunch.App;

public sealed partial class AppsPage : Page
{
    private sealed class RowRefs
    {
        public required TextBlock Status { get; init; }
        public required ToggleButton Star { get; init; }
    }

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<string, RowRefs> _allRows = new();
    private readonly Dictionary<string, RowRefs> _favRows = new();
    private List<AppDto> _apps = [];
    private Dictionary<string, AppOverride> _overrides = new();
    private string? _selectedId;
    private string? _detailsShownFor;
    private string _query = "";
    private string _allKey = "";
    private string _favKey = "";
    private bool _dragging;
    private bool _working;

    public AppsPage()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { await RefreshAsync(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();

        SearchBox.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            _query = SearchBox.Text.Trim();
            RebuildAll();
        };
        AllList.SelectionChanged += (_, _) => OnSelected((AllList.SelectedItem as ListViewItem)?.Tag as string);
        FavList.SelectionChanged += (_, _) => OnSelected((FavList.SelectedItem as ListViewItem)?.Tag as string);
        FavList.DragItemsStarting += (_, _) => _dragging = true;
        FavList.DragItemsCompleted += async (_, _) =>
        {
            _dragging = false;
            var ids = FavList.Items.OfType<ListViewItem>().Select(i => (string)i.Tag).ToList();
            await SafeAsync(() => ServiceClient.SetFavoritesAsync(ids));
        };
        StartButton.Click += async (_, _) => await ActAsync("start");
        RestartButton.Click += async (_, _) => await ActAsync("restart");
        StopButton.Click += async (_, _) => await ActAsync("stop");
        SaveButton.Click += async (_, _) => await SaveOverrideAsync();
    }

    private async Task SafeAsync(Func<Task> work)
    {
        try
        {
            await work();
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is RpcException or IOException or TimeoutException)
        {
            ErrorText.Text = Ui.ErrorText(ex);
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private async Task RefreshAsync()
    {
        if (_working) return;
        await SafeAsync(async () =>
        {
            _apps = await ServiceClient.GetAppsAsync();
            _overrides = (await ServiceClient.GetOverridesAsync()).ToDictionary(o => o.AppId);
        });
        if (ErrorText.Visibility == Visibility.Visible) return;

        var allKey = string.Join(',', Filtered().Select(a => a.Id));
        if (allKey != _allKey) RebuildAll();
        var favKey = string.Join(',', FavoriteIds());
        if (favKey != _favKey && !_dragging) RebuildFavorites();

        UpdateRows(_allRows);
        UpdateRows(_favRows);
        UpdateDetails();
    }

    private IEnumerable<AppDto> Filtered() =>
        string.IsNullOrEmpty(_query) ? _apps : _apps.Where(a => a.Name.Contains(_query, StringComparison.OrdinalIgnoreCase));

    private List<string> FavoriteIds() => _apps.Where(a => a.Favorite).Select(a => a.Id).ToList();

    // ───────────── rows ─────────────

    private (ListViewItem Item, RowRefs Refs) BuildRow(AppDto app)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 6, 4, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var initial = Ui.Label(app.Name.Length > 0 ? app.Name[..1].ToUpperInvariant() : "?", 16, bold: true, secondary: true);
        initial.HorizontalAlignment = HorizontalAlignment.Center;
        initial.VerticalAlignment = VerticalAlignment.Center;
        var image = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
        var iconHost = new Grid { Width = 40, Height = 40 };
        iconHost.Children.Add(initial);
        iconHost.Children.Add(image);
        grid.Children.Add(iconHost);
        _ = ApplyIconAsync(app, image, initial);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Label(app.Name, 14, bold: true));
        var status = Ui.Label("", 12, secondary: true);
        text.Children.Add(status);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var star = new ToggleButton { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(star, "Favorite");
        star.Click += async (_, _) => await ToggleFavoriteAsync(app.Id);
        Grid.SetColumn(star, 2);
        grid.Children.Add(star);

        var item = new ListViewItem { Content = grid, Tag = app.Id };
        return (item, new RowRefs { Status = status, Star = star });
    }

    private static async Task ApplyIconAsync(AppDto app, Image image, TextBlock initial)
    {
        var bmp = await IconCache.GetAsync(app);
        if (bmp is null) return;
        image.Source = bmp;
        initial.Visibility = Visibility.Collapsed;
    }

    private void RebuildAll()
    {
        var keepSelection = _selectedId;
        AllList.Items.Clear();
        _allRows.Clear();
        var list = Filtered().ToList();
        _allKey = string.Join(',', list.Select(a => a.Id));
        foreach (var app in list)
        {
            var (item, refs) = BuildRow(app);
            AllList.Items.Add(item);
            _allRows[app.Id] = refs;
            if (app.Id == keepSelection) AllList.SelectedItem = item;
        }
        UpdateRows(_allRows);
    }

    private void RebuildFavorites()
    {
        var keepSelection = _selectedId;
        FavList.Items.Clear();
        _favRows.Clear();
        var favorites = _apps.Where(a => a.Favorite).ToList();
        _favKey = string.Join(',', favorites.Select(a => a.Id));
        foreach (var app in favorites)
        {
            var (item, refs) = BuildRow(app);
            FavList.Items.Add(item);
            _favRows[app.Id] = refs;
            if (app.Id == keepSelection) FavList.SelectedItem = item;
        }
        UpdateRows(_favRows);
    }

    private void UpdateRows(Dictionary<string, RowRefs> rows)
    {
        foreach (var app in _apps)
        {
            if (!rows.TryGetValue(app.Id, out var refs)) continue;
            var word = Ui.Word(app);
            refs.Status.Text = app.Status.Running ? $"{word} · {Ui.Uptime(app.Status.UptimeSeconds)}" : word;
            if (refs.Star.IsChecked != app.Favorite)
            {
                refs.Star.IsChecked = app.Favorite;
            }
            ((FontIcon)refs.Star.Content).Glyph = app.Favorite ? "" : "";
        }
    }

    private async Task ToggleFavoriteAsync(string id)
    {
        var ids = FavoriteIds();
        if (!ids.Remove(id)) ids.Add(id);
        await SafeAsync(() => ServiceClient.SetFavoritesAsync(ids));
        await RefreshAsync();
    }

    // ───────────── details ─────────────

    private void OnSelected(string? id)
    {
        if (id is null || id == _selectedId) return;
        _selectedId = id;
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        var app = _apps.FirstOrDefault(a => a.Id == _selectedId);
        if (app is null) { Details.Visibility = Visibility.Collapsed; return; }
        Details.Visibility = Visibility.Visible;

        DetailName.Text = app.Name;
        var word = Ui.Word(app);
        DetailStatus.Text = app.Status.Running
            ? $"{word} · {Ui.Uptime(app.Status.UptimeSeconds)} · {app.Status.ProcessCount} process(es)"
            : word;
        var busy = app.Phase is "starting" or "stopping" || _working;
        StartButton.IsEnabled = !busy && !(app.Trackable && app.Status.Running);
        RestartButton.IsEnabled = !busy && app.Trackable && app.Status.Running;
        StopButton.IsEnabled = !busy && app.Trackable && app.Status.Running;

        if (_detailsShownFor != app.Id)
        {
            _detailsShownFor = app.Id;
            ActionMessage.Text = "";
            SaveMessage.Text = "";
            DetailIcon.Source = null;
            _ = ShowDetailIconAsync(app);
            _overrides.TryGetValue(app.Id, out var o);
            ArgsBox.Text = o?.Args ?? "";
            ExtraBox.Text = string.Join(", ", o?.ExtraProcesses ?? []);
            CloseBox.Value = o?.CloseTimeoutMs ?? CatalogueBuilder.DefaultCloseTimeoutMs;
        }
    }

    private async Task ShowDetailIconAsync(AppDto app)
    {
        var bmp = await IconCache.GetAsync(app);
        if (_detailsShownFor == app.Id) DetailIcon.Source = bmp;
    }

    private async Task ActAsync(string kind)
    {
        var app = _apps.FirstOrDefault(a => a.Id == _selectedId);
        if (app is null) return;
        _working = true;
        UpdateDetails();
        ActionMessage.Text = kind switch { "start" => "Starting…", "stop" => "Stopping…", _ => "Restarting…" };
        try
        {
            var result = await ServiceClient.ActAsync(app.Id, kind);
            ActionMessage.Text = result.Ok
                ? $"{app.Name} {kind switch { "start" => "started", "stop" => "stopped", _ => "restarted" }}."
                : result.Message ?? "That didn't work.";
        }
        catch (Exception ex) when (ex is RpcException or IOException or TimeoutException)
        {
            ActionMessage.Text = Ui.ErrorText(ex);
        }
        finally
        {
            _working = false;
        }
        await RefreshAsync();
    }

    private async Task SaveOverrideAsync()
    {
        if (_selectedId is null) return;
        var extras = ExtraBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var timeout = double.IsNaN(CloseBox.Value) ? (int?)null : (int)CloseBox.Value;
        var o = new AppOverride(_selectedId, string.IsNullOrWhiteSpace(ArgsBox.Text) ? null : ArgsBox.Text.Trim(), extras, timeout);
        await SafeAsync(async () =>
        {
            await ServiceClient.SetOverrideAsync(o);
            SaveMessage.Text = "Saved.";
        });
        await RefreshAsync();
    }
}
