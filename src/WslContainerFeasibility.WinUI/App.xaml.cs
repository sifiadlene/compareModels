using Microsoft.UI.Xaml;

namespace WslContainerFeasibility.WinUI;

/// <summary>Starts the feasibility window.</summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>Initializes the WinUI application resources.</summary>
    public App()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
