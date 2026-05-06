using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CrashlessLLM.Interop;

namespace CrashlessLLM.AvaloniaDemo;

public partial class MainWindow : Window
{
    private LLMSession? _session;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        GenerateButton.Click += OnGenerateClick;
        CancelButton.Click += OnCancelClick;
        Closing += OnWindowClosing;
    }

    private async void OnGenerateClick(object? sender, RoutedEventArgs e)
    {
        if (_cts != null)
        {
            return;
        }

        string modelPath = ModelPathTextBox.Text?.Trim() ?? "";
        string prompt = PromptTextBox.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(modelPath))
        {
            OutputTextBox.Text = "Error: Model path is required.";
            return;
        }

        _cts = new CancellationTokenSource();
        GenerateButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        OutputTextBox.Text = "";

        try
        {
            if (_session == null)
            {
                _session = LLM.LoadSafe(modelPath);
            }

            await foreach (string token in _session.StreamAsync(prompt, _cts.Token))
            {
                // Avalonia-safe UI update from async enumerator.
                // The native callback runs on a background thread, but the
                // await foreach continuation respects the caller's SynchronizationContext
                // (in Avalonia, this posts to the UI thread dispatcher).
                OutputTextBox.Text += token;
            }
        }
        catch (OperationCanceledException)
        {
            OutputTextBox.Text += "\n[Cancelled]";
        }
        catch (InsufficientHardwareMemoryException ex)
        {
            OutputTextBox.Text = $"[OOM] {ex.Message}";
            if (ex.Diagnostics is { } d)
            {
                OutputTextBox.Text +=
                    $"\n  Model: {d.ModelFileBytes:N0} B" +
                    $"\n  KV estimate: {d.EstimatedKvCacheBytes:N0} B" +
                    $"\n  Predicted total: {d.PredictedTotalBytes:N0} B" +
                    $"\n  Available: {d.AvailableMemoryBytes:N0} B";
            }
        }
        catch (NativeInferenceException ex)
        {
            OutputTextBox.Text = $"[Native Error] {ex.Message} (code={ex.ErrorCode})";
        }
        catch (Exception ex)
        {
            OutputTextBox.Text = $"[Error] {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            GenerateButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Ensure the native session is disposed before the window closes,
        // preventing a dangling native worker from outliving the UI process.
        _cts?.Cancel();
        _session?.Dispose();
        _session = null;
    }
}
