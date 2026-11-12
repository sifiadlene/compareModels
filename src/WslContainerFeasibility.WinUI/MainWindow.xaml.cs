using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace WslContainerFeasibility.WinUI;

/// <summary>Provides prerequisite checks and controlled fixture execution.</summary>
public sealed partial class MainWindow : Window
{
    private readonly WslFixtureRunner _runner = new();
    private CancellationTokenSource? _cancellation;
    private Task<FixtureRunResult>? _runTask;
    private bool _waitingForClose;
    private bool _closeReady;

    /// <summary>Initializes the feasibility window.</summary>
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Closing += AppWindow_Closing;
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs args)
    {
        CheckButton.IsEnabled = false;
        RunButton.IsEnabled = false;
        PrerequisiteText.Text = "Checking prerequisites.";
        try
        {
            var result = await Task.Run(_runner.CheckPrerequisites);
            PrerequisiteText.Text = result.Details;
        }
        catch (Exception exception)
        {
            PrerequisiteText.Text = $"Prerequisite check failed: {exception.Message}";
        }
        finally
        {
            CheckButton.IsEnabled = true;
            RunButton.IsEnabled = true;
        }
    }

    private async void RunButton_Click(object sender, RoutedEventArgs args)
    {
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        CheckButton.IsEnabled = false;
        RunButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        OutcomeText.Text = "Running";
        StatusText.Text = "Starting prerequisite checks.";
        EvidenceText.Text = "Evidence will be saved after cleanup.";

        var progress = new Progress<string>(message =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_closeReady && _runTask is { IsCompleted: false })
                {
                    StatusText.Text = message;
                }
            });
        });
        _runTask = Task.Run(() => _runner.RunAsync(progress, cancellation.Token));
        try
        {
            var result = await _runTask;
            if (!_closeReady)
            {
                OutcomeText.Text = result.Outcome.ToString();
                StatusText.Text = result.Summary;
                EvidenceText.Text = result.EvidencePath;
            }
        }
        catch (Exception exception)
        {
            if (!_closeReady)
            {
                OutcomeText.Text = "Failed";
                StatusText.Text = $"The run or evidence persistence failed: {exception.Message}";
                EvidenceText.Text = "Evidence could not be saved.";
            }
        }
        finally
        {
            _runTask = null;
            cancellation.Dispose();
            _cancellation = null;
            if (!_closeReady)
            {
                CheckButton.IsEnabled = true;
                RunButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
            }
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs args)
    {
        _cancellation?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancellation requested; waiting for cleanup.";
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeReady || _runTask is null)
        {
            return;
        }

        args.Cancel = true;
        if (_waitingForClose)
        {
            return;
        }

        _waitingForClose = true;
        _cancellation?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancellation requested; waiting for cleanup before closing.";
        try
        {
            var result = await _runTask;
            if (result.Outcome == FixtureOutcome.Failed)
            {
                _waitingForClose = false;
                return;
            }

            _closeReady = true;
            Close();
        }
        catch (Exception exception)
        {
            OutcomeText.Text = "Failed";
            StatusText.Text = $"Cleanup or evidence persistence failed: {exception.Message}";
            _waitingForClose = false;
        }
    }
}
