using Microsoft.UI.Xaml;

namespace ReLaunch.App;

/// <summary>
/// `ReLaunch.App.exe --selftest`: opens every page in turn, waits for it to load, and exits 0 when nothing threw.
/// Used by CI to catch XAML / startup failures that compiling alone cannot find.
/// </summary>
internal static class SelfTest
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "selftest.log");
    private static bool _failed;

    private static void Log(string line)
    {
        File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
    }

    public static void Run(Application app, MainWindow window)
    {
        File.WriteAllText(LogPath, "");
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
                    var page = window.CurrentPage?.Name ?? "none";
                    Log($"page {tag}: {page}");
                    if (window.CurrentPage is null) _failed = true;
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
}
