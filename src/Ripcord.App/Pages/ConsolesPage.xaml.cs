using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ripcord_App.Dialogs;
using Ripcord_App.Services;

namespace Ripcord_App.Pages;

public sealed partial class ConsolesPage : Page
{
    private readonly PairedConsoleStore _store = new();

    public ConsolesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        List<PairedConsole> consoles = _store.Load();
        ConsoleList.ItemsSource = consoles;

        bool any = consoles.Count > 0;
        ConsoleList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnAddConsoleClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new PairConsoleDialog { XamlRoot = XamlRoot };
            ContentDialogResult result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary && dialog.Result is { } paired)
            {
                _store.Upsert(paired);
                Refresh();
            }
        }
        catch (Exception ex)
        {
            // An event handler must not let an exception reach the dispatcher unhandled.
            await ShowErrorAsync("Couldn't add that console", ex.Message);
        }
    }

    private async void OnRemoveConsoleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string host })
        {
            return;
        }

        try
        {
            // Removing a pairing is destructive and not undoable — it discards the credential, so getting the
            // console back means re-entering a link code on the console itself. It previously happened
            // instantly on a single click, next to the Connect button.
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Remove this console?",
                Content = $"Ripcord will forget its pairing with {host}. To use it again you'll need to enter a "
                          + "new link code from the console.",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                _store.Remove(host);
                Refresh();
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Couldn't remove that console", ex.Message);
        }
    }

    private void OnConnectConsoleClick(object sender, RoutedEventArgs e)
    {
        // The stream is a window-level layer above the navigation chrome, not a page inside it — see
        // MainWindow.ShowStream. SessionPage then owns a SessionController for the whole lifecycle.
        if (sender is Button { Tag: PairedConsole console } && App.MainWindow is MainWindow main)
        {
            main.ShowStream(console);
        }
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        try
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = message,
                CloseButtonText = "OK",
            }.ShowAsync();
        }
        catch (Exception)
        {
            // Another dialog is already open; the original error is more important than reporting this.
        }
    }
}
