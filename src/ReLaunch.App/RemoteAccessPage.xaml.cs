using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ReLaunch.Core.Pipes;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace ReLaunch.App;

public sealed partial class RemoteAccessPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _key;
    private bool _shown;
    private bool _urlLoaded;

    public RemoteAccessPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => { await RefreshAsync(); await LoadKeyAsync(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += async (_, _) => await RefreshAsync();
        SaveUrlButton.Click += async (_, _) => await SaveUrlAsync();
        GenerateButton.Click += async (_, _) => await GenerateAsync();
        CopyButton.Click += (_, _) => CopyKey();
        ShowButton.Click += (_, _) => { _shown = !_shown; ShowButton.Content = _shown ? "Hide" : "Show"; RenderKey(); };
    }

    private async Task RefreshAsync()
    {
        try
        {
            var s = await ServiceClient.GetStateAsync();
            if (s is null) return;
            if (!_urlLoaded) { UrlBox.Text = s.RelayUrl; _urlLoaded = true; }

            (string text, string detail, Color color) = s.RelayState switch
            {
                "connected" => ("Connected", s.LastHeartbeat is { } hb ? $"Last heartbeat {Ui.Ago(hb)}." : "Connected to the relay.", Colors.SeaGreen),
                "connecting" => ("Connecting", "Reaching the relay…", Colors.Goldenrod),
                "reconnecting" => ("Reconnecting", s.LastError ?? "The connection dropped. Trying again.", Colors.Goldenrod),
                "rejected" => ("Rejected key", "The relay does not accept this PC key. Set the same key as the PC_KEY secret on the relay.", Colors.IndianRed),
                _ => ("Not set up", s.HasKey ? "Enter the relay URL to connect." : "Generate a key and enter the relay URL.", Colors.Gray),
            };
            StateText.Text = text;
            StateDetail.Text = detail;
            StateDot.Fill = new SolidColorBrush(color);
        }
        catch (Exception ex) when (ex is RpcException or IOException or TimeoutException)
        {
            StateText.Text = "Service not reachable";
            StateDetail.Text = Ui.ErrorText(ex);
            StateDot.Fill = new SolidColorBrush(Colors.IndianRed);
        }
    }

    private async Task LoadKeyAsync()
    {
        try { _key = await ServiceClient.GetKeyAsync(); }
        catch (Exception ex) when (ex is RpcException or IOException or TimeoutException) { _key = null; }
        RenderKey();
    }

    private void RenderKey()
    {
        KeyBox.Text = _key is null ? "No key yet" : _shown ? _key : new string('•', 24);
        CopyButton.IsEnabled = _key is not null;
        ShowButton.IsEnabled = _key is not null;
    }

    private async Task SaveUrlAsync()
    {
        try
        {
            await ServiceClient.SetRelayUrlAsync(UrlBox.Text);
            UrlMessage.Text = "Saved.";
        }
        catch (Exception ex) when (ex is RpcException or IOException or TimeoutException) { UrlMessage.Text = Ui.ErrorText(ex); }
        await RefreshAsync();
    }

    private async Task GenerateAsync()
    {
        if (_key is not null)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Replace the PC key?",
                Content = "The relay will reject this PC until you set the new key there too.",
                PrimaryButtonText = "Replace",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        try
        {
            _key = await ServiceClient.GenerateKeyAsync();
            _shown = true;
            ShowButton.Content = "Hide";
            RenderKey();
            KeyMessage.Text = "New key created. Copy it to the relay.";
        }
        catch (Exception ex) when (ex is RpcException or IOException or TimeoutException) { KeyMessage.Text = Ui.ErrorText(ex); }
        await RefreshAsync();
    }

    private void CopyKey()
    {
        if (_key is null) return;
        var package = new DataPackage();
        package.SetText(_key);
        Clipboard.SetContent(package);
        KeyMessage.Text = "Copied.";
    }
}
