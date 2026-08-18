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
                IsEnabled = false;
                _ = DisposeAndCloseAsync(disposable);
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
                Trace.TraceError("FileOrganizer initialization failed: {0}", exception);
            }
        }
    }

    private async Task DisposeAndCloseAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync();
        }
        catch (Exception exception)
        {
            Trace.TraceError("FileOrganizer shutdown failed: {0}", exception);
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }
}
