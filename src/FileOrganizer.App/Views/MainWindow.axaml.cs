using System.Diagnostics;
using Avalonia.Controls;
using FileOrganizer.App.ViewModels;

namespace FileOrganizer.App.Views;

public partial class MainWindow : Window
{
    private bool _shutdownStarted;
    private bool _shutdownComplete;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_shutdownComplete && DataContext is IAsyncDisposable disposable)
        {
            e.Cancel = true;
            if (!_shutdownStarted)
            {
                _shutdownStarted = true;
                _ = ConfirmDisposeAndCloseAsync(disposable);
            }
        }

        base.OnClosing(e);
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        Opened -= OnOpened;
        if (DataContext is MainWindowViewModel viewModel)
        {
            try
            {
                await viewModel.InitializeAsync();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                TraceErrorSafely("FileOrganizer initialization failed: {0}", exception);
            }
        }
    }

    private async Task ConfirmDisposeAndCloseAsync(IAsyncDisposable disposable)
    {
        if (disposable is MainWindowViewModel viewModel)
        {
            bool closeApproved;
            try
            {
                closeApproved = await viewModel.RequestCloseAsync();
            }
            catch (Exception exception)
            {
                TraceErrorSafely("FileOrganizer close confirmation failed: {0}", exception);
                closeApproved = false;
            }

            if (!closeApproved)
            {
                _shutdownStarted = false;
                return;
            }
        }

        IsEnabled = false;
        try
        {
            await disposable.DisposeAsync();
        }
        catch (Exception exception)
        {
            TraceErrorSafely("FileOrganizer shutdown failed: {0}", exception);
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }

    private static void TraceErrorSafely(string message, Exception exception)
    {
        try
        {
            Trace.TraceError(message, exception);
        }
        catch (Exception)
        {
            // A diagnostic listener cannot be allowed to strand the close state machine.
        }
    }
}
