using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using ReLaunch.Core.Contracts;
using ReLaunch.Core.Pipes;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace ReLaunch.App;

internal static class Ui
{
    public static Microsoft.UI.Xaml.Media.Brush ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Microsoft.UI.Xaml.Media.Brush brush
            ? brush
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public static TextBlock Label(string text, double size = 14, bool bold = false, bool secondary = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis };
        if (bold) t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        if (secondary) t.Opacity = 0.7;
        return t;
    }

    public static string Ago(DateTimeOffset? time)
    {
        if (time is null) return "never";
        var d = DateTimeOffset.UtcNow - time.Value;
        if (d.TotalSeconds < 45) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        return time.Value.ToLocalTime().ToString("MMM d, HH:mm");
    }

    public static string Uptime(long seconds)
    {
        if (seconds < 60) return "under a minute";
        var m = seconds / 60; var h = m / 60; var d = h / 24;
        return d > 0 ? $"{d}d {h % 24}h" : h > 0 ? $"{h}h {m % 60}m" : $"{m} min";
    }

    public static string Word(AppDto a) => a.Phase switch
    {
        "stopping" => "Stopping",
        "starting" => "Starting",
        _ => !a.Trackable ? "Ready" : a.Status.Running ? "Running" : "Stopped",
    };

    public static string ErrorText(Exception ex) => ex is RpcException { Code: "timeout" or "closed" } or IOException or TimeoutException
        ? "The reLAUNCH service is not responding. Check that it is running."
        : ex.Message;
}

internal static class IconCache
{
    private static readonly Dictionary<string, BitmapImage?> Cache = new();

    public static async Task<BitmapImage?> GetAsync(AppDto app)
    {
        if (app.IconHash is null) return null;
        var key = app.Id + ":" + app.IconHash;
        if (Cache.TryGetValue(key, out var hit)) return hit;

        BitmapImage? image = null;
        try
        {
            var b64 = await ServiceClient.GetIconAsync(app.Id);
            if (!string.IsNullOrEmpty(b64))
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(Convert.FromBase64String(b64).AsBuffer());
                stream.Seek(0);
                image = new BitmapImage();
                await image.SetSourceAsync(stream);
            }
        }
        catch (Exception ex) when (ex is RpcException or FormatException or IOException)
        {
            image = null;
        }
        Cache[key] = image;
        return image;
    }
}
