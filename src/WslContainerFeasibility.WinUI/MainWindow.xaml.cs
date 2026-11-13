using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;
using System.Diagnostics;

namespace WslContainerFeasibility.WinUI;

/// <summary>Provides prerequisite checks and controlled fixture execution.</summary>
public sealed partial class MainWindow : Window
{
    private readonly WslFixtureRunner _runner = new();
    private readonly CopilotRunRunner _copilotRunner = new();
    private CancellationTokenSource? _cancellation;
    private Task<FixtureRunResult>? _runTask;
    private Task<CopilotRunResult>? _copilotRunTask;
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
        SetRunControlsEnabled(false);
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
            SetRunControlsEnabled(true);
        }
    }

    private async void RunButton_Click(object sender, RoutedEventArgs args)
    {
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetRunControlsEnabled(false);
        CancelButton.IsEnabled = true;
        OutcomeText.Text = "Running";
        StatusText.Text = "Starting prerequisite checks.";
        EvidenceText.Text = "Evidence will be saved after cleanup.";

        var progress = new Progress<string>(message =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_closeReady && (_runTask is { IsCompleted: false }
                                     || _copilotRunTask is { IsCompleted: false }))
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
                SetRunControlsEnabled(true);
                CancelButton.IsEnabled = false;
            }
        }
    }

    private void SignInButton_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            using Process process = _copilotRunner.StartOfficialSignIn();
            CopilotStatusText.Text =
                "The official Copilot CLI login flow was started. Complete sign-in there, then load strategies.";
        }
        catch (Win32Exception exception)
        {
            CopilotStatusText.Text =
                $"The Copilot CLI is not available. Install it, then use its official 'copilot login' flow: {exception.Message}";
        }
        catch (Exception exception)
        {
            CopilotStatusText.Text = $"Could not start Copilot CLI sign-in: {exception.Message}";
        }
    }

    private async void LoadStrategiesButton_Click(object sender, RoutedEventArgs args)
    {
        SetRunControlsEnabled(false);
        CopilotStatusText.Text = "Loading strategies from the signed-in Copilot account.";
        try
        {
            var strategies = await Task.Run(() => _copilotRunner.ListStrategiesAsync());
            StrategyComboBox.ItemsSource = strategies
                .Select(strategy => new ComboBoxItem
                {
                    Content = $"{strategy.Name} ({strategy.Id})",
                    Tag = strategy.Id
                })
                .ToArray();
            StrategyComboBox.SelectedIndex = strategies.Count == 0 ? -1 : 0;
            CopilotStatusText.Text = strategies.Count == 0
                ? "The signed-in account returned no available strategies."
                : $"Loaded {strategies.Count} available strategies.";
        }
        catch (Exception exception)
        {
            StrategyComboBox.ItemsSource = null;
            CopilotStatusText.Text =
                $"Could not load strategies. Sign in with the official Copilot CLI, then retry: {exception.Message}";
        }
        finally
        {
            SetRunControlsEnabled(true);
        }
    }

    private void StrategyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        RunCopilotButton.IsEnabled = !_waitingForClose && _copilotRunTask is null
            && StrategyComboBox.SelectedItem is ComboBoxItem { Tag: string };
    }

    private async void RunCopilotButton_Click(object sender, RoutedEventArgs args)
    {
        if (StrategyComboBox.SelectedItem is not ComboBoxItem { Tag: string strategyId })
        {
            CopilotStatusText.Text = "Select an available model strategy before running.";
            return;
        }

        var confirmation = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Confirm one Copilot attempt",
            Content = $"This sends one request to {strategyId} and may consume billable Copilot usage. " +
                "The attempt can edit only a disposable WSL fixture, has no task network or host-file access, and cannot publish changes.",
            PrimaryButtonText = "Run once",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            CopilotStatusText.Text = "Not started. No Copilot request was sent.";
            return;
        }

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetRunControlsEnabled(false);
        CancelButton.IsEnabled = true;
        CopilotOutcomeText.Text = "Running";
        CopilotStatusText.Text = "Starting prerequisite checks.";
        CopilotEvidenceText.Text = "Evidence will be saved after cleanup.";
        CopilotPatchText.Text = string.Empty;

        var progress = new Progress<string>(message =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_closeReady && _copilotRunTask is { IsCompleted: false })
                {
                    CopilotStatusText.Text = message;
                }
            });
        });
        _copilotRunTask = Task.Run(() => _copilotRunner.RunOnceAsync(
            strategyId,
            userApproved: true,
            progress,
            cancellation.Token));
        try
        {
            var result = await _copilotRunTask;
            if (!_closeReady)
            {
                CopilotOutcomeText.Text = result.Outcome.ToString();
                CopilotStatusText.Text = result.Summary;
                CopilotEvidenceText.Text = result.EvidencePath;
                CopilotPatchText.Text = result.Patch ?? "No repository change was captured.";
            }
        }
        catch (Exception exception)
        {
            if (!_closeReady)
            {
                CopilotOutcomeText.Text = "Failed";
                CopilotStatusText.Text = $"The Copilot run or evidence persistence failed: {exception.Message}";
                CopilotEvidenceText.Text = "Evidence could not be saved.";
            }
        }
        finally
        {
            _copilotRunTask = null;
            cancellation.Dispose();
            _cancellation = null;
            if (!_closeReady && !_waitingForClose)
            {
                SetRunControlsEnabled(true);
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
        if (_closeReady || (_runTask is null && _copilotRunTask is null))
        {
            return;
        }

        args.Cancel = true;
        if (_waitingForClose)
        {
            return;
        }

        _waitingForClose = true;
        Task<FixtureRunResult>? fixtureTask = _runTask;
        Task<CopilotRunResult>? copilotTask = _copilotRunTask;
        _cancellation?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancellation requested; waiting for cleanup before closing.";
        try
        {
            if (fixtureTask is not null)
            {
                var result = await fixtureTask;
                if (result.Outcome == FixtureOutcome.Failed)
                {
                    _waitingForClose = false;
                    return;
                }
            }

            if (copilotTask is not null)
            {
                var result = await copilotTask;
                if (result.Outcome == FixtureOutcome.Failed)
                {
                    _waitingForClose = false;
                    return;
                }
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

    private void SetRunControlsEnabled(bool enabled)
    {
        bool controlsEnabled = enabled && !_waitingForClose;
        CheckButton.IsEnabled = controlsEnabled;
        RunButton.IsEnabled = controlsEnabled;
        SignInButton.IsEnabled = controlsEnabled;
        LoadStrategiesButton.IsEnabled = controlsEnabled;
        StrategyComboBox.IsEnabled = controlsEnabled;
        RunCopilotButton.IsEnabled = controlsEnabled
            && StrategyComboBox.SelectedItem is ComboBoxItem { Tag: string };
    }
}
