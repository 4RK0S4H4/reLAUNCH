using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using ReLaunch.Core.Pipes;

namespace ReLaunch.App;

public sealed partial class SettingsPage : Page
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "reLAUNCH";
    private string _dataFolder = "";
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
        StartupSwitch.Toggled += (_, _) => { if (!_loading) SetStartup(StartupSwitch.IsOn); };
        DebugSwitch.Toggled += async (_, _) =>
        {
            if (_loading) return;
            try { await ServiceClient.SetDebugApiAsync(DebugSwitch.IsOn); }
            catch (Exception ex) when (ex is RpcException or IOException or TimeoutException) { DebugNote.Text = Ui.ErrorText(ex); }
        };
        FolderButton.Click += (_, _) =>
        {
            if (_dataFolder.Length > 0) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_dataFolder}\"") { UseShellExecute = true });
        };
    }

    private async Task LoadAsync()
    {
        _loading = true;
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            StartupSwitch.IsOn = key?.GetValue(RunValue) is string;

        try
        {
            var s = await ServiceClient.GetStateAsync();
            if (s is not null)
            {
                DebugSwitch.IsOn = s.DebugApi;
                _dataFolder = s.DataFolder;
                AboutText.Text = $"reLAUNCH {s.Version}\nPC name: {s.PcName}\nSession: {s.Session}\nApps in catalogue: {s.AppCount}\nData folder: {s.DataFolder}";
            }
        }
        catch (Exception ex) when (ex is RpcException or IOException or TimeoutException)
        {
            AboutText.Text = Ui.ErrorText(ex);
        }
        _loading = false;
    }

    private static void SetStartup(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        if (on && Environment.ProcessPath is { } path) key.SetValue(RunValue, $"\"{path}\"");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }
}
