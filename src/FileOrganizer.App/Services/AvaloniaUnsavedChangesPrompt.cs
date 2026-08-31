using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace FileOrganizer.App.Services;

/// <summary>Shows an owner-modal Save, Discard, or Cancel close prompt.</summary>
public sealed class AvaloniaUnsavedChangesPrompt : IUnsavedChangesPrompt
{
    private readonly Window _owner;

    public AvaloniaUnsavedChangesPrompt(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public async ValueTask<UnsavedChangesDecision> PromptAsync(
        string configurationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        cancellationToken.ThrowIfCancellationRequested();

        Window dialog = CreateDialog(configurationPath);
        using CancellationTokenRegistration cancellationRegistration =
            cancellationToken.UnsafeRegister(
                static state => RequestCancellationClose((Window)state!),
                dialog);
        UnsavedChangesDecision decision = await dialog.ShowDialog<UnsavedChangesDecision>(_owner);
        cancellationToken.ThrowIfCancellationRequested();
        return Enum.IsDefined(decision)
            ? decision
            : UnsavedChangesDecision.Cancel;
    }

    private static void RequestCancellationClose(Window dialog)
    {
        try
        {
            Dispatcher.UIThread.Post(() => CloseForCancellation(dialog));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher shutdown already ended the owning UI lifetime.
        }
    }

    private static void CloseForCancellation(Window dialog)
    {
        try
        {
            if (dialog.IsVisible)
            {
                dialog.Close(UnsavedChangesDecision.Cancel);
            }
        }
        catch (InvalidOperationException)
        {
            // A concurrent owner shutdown already ended the modal operation.
        }
    }

    private static Window CreateDialog(string configurationPath)
    {
        var dialog = new Window
        {
            Title = "Unsaved configuration",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var saveButton = new Button
        {
            Content = "Save",
            MinWidth = 92,
        };
        var discardButton = new Button
        {
            Content = "Discard",
            MinWidth = 92,
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 92,
        };

        saveButton.Click += (_, _) => dialog.Close(UnsavedChangesDecision.Save);
        discardButton.Click += (_, _) => dialog.Close(UnsavedChangesDecision.Discard);
        cancelButton.Click += (_, _) => dialog.Close(UnsavedChangesDecision.Cancel);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = "The configuration draft has unsaved changes.",
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 18,
                },
                new TextBlock
                {
                    Text = configurationPath,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = "Save the draft before closing FileOrganizer?",
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children =
                    {
                        saveButton,
                        discardButton,
                        cancelButton,
                    },
                },
            },
        };

        return dialog;
    }
}
