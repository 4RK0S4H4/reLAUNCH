using Microsoft.UI.Xaml;

namespace ReLaunch.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        _window = new MainWindow();
        _window.Activate();

        if (commandLine.Contains("--selftest")) SelfTest.Run(this, _window);
    }
}
