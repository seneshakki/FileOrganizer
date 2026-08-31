using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FileOrganizer.App.Services;
using FileOrganizer.App.Startup;
using FileOrganizer.App.ViewModels;
using FileOrganizer.App.Views;
using FileOrganizer.Application.Configuration;
using FileOrganizer.Infrastructure.Configuration;

namespace FileOrganizer.App;

public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var configurationService = new OrganizationConfigurationService(
                new FileConfigurationTextStore());
            var mainWindow = new MainWindow();
            var viewModel = new MainWindowViewModel(
                ConfigurationPathArguments.Parse(desktop.Args ?? []),
                configurationService,
                new WindowsWatchPreviewRuntimeFactory(),
                new AvaloniaUiDispatcher(),
                new AvaloniaConfigurationPathPicker(mainWindow));
            mainWindow.DataContext = viewModel;
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
