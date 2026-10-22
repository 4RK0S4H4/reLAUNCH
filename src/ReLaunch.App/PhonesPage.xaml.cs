using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using ReLaunch.Core.Contracts;
using ReLaunch.Core.Pipes;
using Windows.Storage.Streams;

namespace ReLaunch.App;

public sealed partial class PhonesPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset? _expires;
    private int _tick;

    public PhonesPage()
    {
        InitializeComponent();
        PairButton.Click += async (_, _) => await CreatePairingAsync();
        _timer.Tick += async (_, _) => await OnTickAsync();
        Loaded += async (_, _) => { await LoadDevicesAsync(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>Used by the CI self-test to show a pairing code.</summary>
    public Task PairNowAsync() => CreatePairingAsync();

    private async Task OnTickAsync()
    {
        if (_expires is { } exp)
        {
            var left = exp - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                _expires = null;
                ExpiryText.Text = "This code has expired. Create a new one.";
                QrImage.Source = null;
                CodeText.Text = "";
            }
            else ExpiryText.Text = $"Expires in {(int)left.TotalMinutes}:{left.Seconds:00}";
        }
        if (++_tick % 5 == 0) await LoadDevicesAsync();
    }

    private async Task CreatePairingAsync()
    {
        PairButton.IsEnabled = false;
        try
        {
            var info = await ServiceClient.CreatePairingAsync();
            if (info is null) return;
            Info.IsOpen = false;
            CodeText.Text = string.Join(' ', Chunk(info.Code, 5));
            UrlText.Text = info.Url;
            _expires = DateTimeOffset.FromUnixTimeMilliseconds(info.ExpiresAt);
            await ShowQrAsync(info.Url);
        }
        catch (RpcException ex) when (ex.Code is "notConfigured" or "relayOffline")
        {
            ShowInfo(ex.Code == "notConfigured"
                ? "Remote access is not set up yet. Open Remote Access and enter the relay URL and key first."
                : "The PC is not connected to the relay yet. Check Remote Access.");
        }
        catch (Exception ex) when (ex is RpcException or IOException or TimeoutException)
        {
            ShowInfo(Ui.ErrorText(ex));
        }
        finally
        {
            PairButton.IsEnabled = true;
        }
        await LoadDevicesAsync();
    }

    private static IEnumerable<string> Chunk(string s, int size)
    {
        for (var i = 0; i < s.Length; i += size) yield return s.Substring(i, Math.Min(size, s.Length - i));
    }

    private async Task ShowQrAsync(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(stream);
        QrImage.Source = bmp;
    }

    private void ShowInfo(string message)
    {
        Info.Title = "Can't pair yet";
        Info.Message = message;
        Info.IsOpen = true;
    }

    private async Task LoadDevicesAsync()
    {
        List<DeviceInfo> devices;
        try { devices = await ServiceClient.ListDevicesAsync(); }
        catch (Exception ex) when (ex is RpcException or IOException or TimeoutException)
        {
            if (ex is RpcException { Code: not ("notConfigured" or "relayOffline") } or IOException or TimeoutException)
                ShowInfo(Ui.ErrorText(ex));
            return;
        }

        DeviceList.Children.Clear();
        foreach (var d in devices)
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(8) };
            row.Background = Ui.ThemeBrush("CardBackgroundFillColorDefaultBrush");
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(Ui.Label(d.Name, 14, bold: true));
            var seen = d.LastSeen is { } ls ? DateTimeOffset.FromUnixTimeMilliseconds(ls) : (DateTimeOffset?)null;
            text.Children.Add(Ui.Label($"Paired {DateTimeOffset.FromUnixTimeMilliseconds(d.CreatedAt).ToLocalTime():MMM d, yyyy} · last seen {Ui.Ago(seen)}", 12, secondary: true));
            row.Children.Add(text);
            var revoke = new Button { Content = "Revoke", VerticalAlignment = VerticalAlignment.Center };
            var id = d.Id;
            revoke.Click += async (_, _) =>
            {
                revoke.IsEnabled = false;
                try { await ServiceClient.RevokeDeviceAsync(id); }
                catch (Exception ex) when (ex is RpcException or IOException or TimeoutException) { ShowInfo(Ui.ErrorText(ex)); }
                await LoadDevicesAsync();
            };
            Grid.SetColumn(revoke, 1);
            row.Children.Add(revoke);
            DeviceList.Children.Add(row);
        }
        NoDevices.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
