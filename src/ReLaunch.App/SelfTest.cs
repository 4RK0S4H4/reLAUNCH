using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ReLaunch.App;

/// <summary>
/// `ReLaunch.App.exe --selftest [--shots=DIR]`: opens every page in turn, waits for it to load, and exits 0 when
/// nothing threw. With --shots it also saves a PNG of each page. Used by CI to catch XAML / startup failures that
/// compiling alone cannot find.
/// </summary>
internal static class SelfTest
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "selftest.log");
    private static bool _failed;

    private static void Log(string line)
    {
        File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
    }

    public static void Run(Application app, MainWindow window, string? shotsDir)
    {
        File.WriteAllText(LogPath, "");
        if (shotsDir is not null) Directory.CreateDirectory(shotsDir);
        app.UnhandledException += (_, e) =>
        {
            _failed = true;
            Log($"UNHANDLED: {e.Exception}");
            e.Handled = true;
        };

        window.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await Task.Delay(2500);
                foreach (var tag in MainWindow.PageTags)
                {
                    window.Navigate(tag);
                    await Task.Delay(3500);
                    switch (window.CurrentContent)
                    {
                        case PhonesPage phones: await phones.PairNowAsync(); await Task.Delay(1500); break;
                        case AppsPage apps: apps.SelectForSelfTest("Calculator"); await Task.Delay(1200); break;
                    }
                    var page = window.CurrentPage?.Name ?? "none";
                    Log($"page {tag}: {page}");
                    if (window.CurrentPage is null) _failed = true;
                    if (shotsDir is not null) await CaptureAsync(window, Path.Combine(shotsDir, tag + ".png"));
                }
            }
            catch (Exception ex)
            {
                _failed = true;
                Log($"EXCEPTION: {ex}");
            }
            Log(_failed ? "SELFTEST FAILED" : "SELFTEST OK");
            Environment.Exit(_failed ? 1 : 0);
        });
    }

    private static async Task CaptureAsync(MainWindow window, string path)
    {
        try
        {
            if (window.RootElement is not UIElement root) return;
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(root);
            var pixels = (await bitmap.GetPixelsAsync()).ToArray();

            // The Mica backdrop is not part of the rendered tree: flatten onto a plain page colour.
            var dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
            byte bg = dark ? (byte)0x20 : (byte)0xF3;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                int a = pixels[i + 3];
                pixels[i] = (byte)((pixels[i] + bg * (255 - a) / 255));
                pixels[i + 1] = (byte)((pixels[i + 1] + bg * (255 - a) / 255));
                pixels[i + 2] = (byte)((pixels[i + 2] + bg * (255 - a) / 255));
                pixels[i + 3] = 255;
            }

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
            stream.Seek(0);
            var bytes = new byte[stream.Size];
            await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
            await File.WriteAllBytesAsync(path, bytes);
            Log($"screenshot {Path.GetFileName(path)} {bitmap.PixelWidth}x{bitmap.PixelHeight}");
        }
        catch (Exception ex)
        {
            Log($"screenshot failed: {ex.Message}");
        }
    }
}
