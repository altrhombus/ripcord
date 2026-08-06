using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Ripcord.Core.Consoles;
using Ripcord.Presentation;
using Ripcord.Presentation.Consoles;
using Ripcord_App.Accents;
using Ripcord_App.Converters;
using Ripcord_App.Services;

namespace Ripcord_App.Pages;

public sealed partial class ConsolesPage : Page, IInitialFocusTarget
{
    /// <summary>
    /// Whatever this page's current layout says is the point of it. Open the window, press A, playing.
    ///
    /// <para>
    /// On the hero that is the Play button itself — the layout exists so there is exactly one thing to press.
    /// On the grid it is the grid, not a specific container: focusing a <see cref="GridView"/> hands focus to
    /// its own first (or last-focused) item, which survives the list being rebuilt underneath. With nothing
    /// paired it is null, so the shell falls back to tree order — an empty install has no console to offer and
    /// the add button is genuinely the point.
    /// </para>
    /// </summary>
    public Control? InitialFocus
    {
        get
        {
            if (HeroPanel.Visibility == Visibility.Visible)
            {
                return HeroPlayButton;
            }

            return ConsoleGrid.Visibility == Visibility.Visible ? ConsoleGrid : null;
        }
    }

    private readonly RipcordAppServices _services;
    private readonly IPairedConsoleStore _store;

    // Fills in each card's live reachability, and watches a rest-requested console settle. The probing policy —
    // concurrency, the rest-settle budget, what a transient non-answer means mid-transition — lives in the
    // monitor, where it is unit-tested; this page only decides when to ask.
    private readonly ConsoleReachabilityMonitor _reachability;

    /// <summary>
    /// The grid's items: every paired console, then the add tile. Observable so a rename, a removal or a
    /// newly paired console updates the card in place and animates, instead of the whole list being replaced
    /// underneath the user.
    /// </summary>
    private readonly ObservableCollection<object> _items = [];

    // Cancels the in-flight status probes when the list is rebuilt or the page goes away, so a probe cannot
    // resolve onto a row that has since been replaced.
    private CancellationTokenSource? _probeCts;

    // The console (by host) we asked to rest as we left the last session, so the next Refresh shows it
    // "Going to sleep…" and watches it settle. Null when the last disconnect did not request rest.
    private string? _restRequestedHost;

    public ConsolesPage()
    {
        // Resolved BEFORE InitializeComponent, so compiled bindings evaluate against live objects on their first
        // pass rather than against fields that are still null.
        _services = App.Services;
        _store = _services.Consoles;
        _reachability = _services.CreateReachabilityMonitor();

        InitializeComponent();

        ConsoleGrid.ItemsSource = _items;
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) =>
        {
            CancelProbes();

            // The hero subscribes to its console for live reachability; leaving that attached would keep this
            // page alive through the view-model for as long as the console object lives.
            DetachHero();
        };
    }

    /// <summary>
    /// Called by the window when a stream closes and this page becomes visible again. Fixes the stale-list
    /// problem (the page's own <c>Loaded</c> only fires once, so returning from a stream never re-probed), and
    /// carries the rest-on-disconnect intent so the just-left console can be shown transitioning to rest.
    /// </summary>
    public void OnReturnedFromStream(string? consoleHost, bool restRequested)
    {
        _restRequestedHost = restRequested ? consoleHost : null;
        Refresh();
    }

    /// <summary>
    /// Rebuild the page for whatever is actually paired.
    ///
    /// <para>
    /// Three layouts, because one or two consoles is the real case and a grid is only the right answer for one
    /// of them. Nothing → the empty state, which is the first-run explanation. Exactly one → the hero, where
    /// the console is the page and Play is a real button. Two or more → the card grid with the add tile after
    /// the last card.
    /// </para>
    /// </summary>
    private void Refresh()
    {
        CancelProbes();
        DetachHero();

        List<ConsoleCardViewModel> consoles = _store.Load().Select(_services.CreateConsoleCard).ToList();

        _items.Clear();

        // A grid of one is a list pretending to be a choice, so one console gets its own layout rather than a
        // single card marooned in a wrapping panel.
        bool hero = consoles.Count == 1;
        bool grid = consoles.Count > 1;

        if (grid)
        {
            foreach (ConsoleCardViewModel item in consoles)
            {
                _items.Add(item);
            }

            // Only when there is a grid to put it in; the other two layouts carry their own add affordance, and
            // a lone ghost tile floating in an otherwise blank page says much less.
            _items.Add(AddConsolePlaceholder.Instance);
        }

        ConsoleGrid.Visibility = Vis(grid);
        HeroPanel.Visibility = Vis(hero);
        EmptyState.Visibility = Vis(consoles.Count == 0);

        // The subtitle instructs someone to pick from several. With one console there is nothing to pick.
        SubtitleText.Visibility = Vis(grid);

        if (hero)
        {
            AttachHero(consoles[0]);
        }

        if (consoles.Count > 0)
        {
            // Consume the rest intent for this pass only; a later plain Refresh must not re-arm the watch.
            string? restHost = _restRequestedHost;
            _restRequestedHost = null;

            _probeCts = new CancellationTokenSource();
            _ = _reachability.RefreshAsync(consoles, restHost, _probeCts.Token);
        }

        ApplyColumnCount();
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    // ---- hero layout -----------------------------------------------------------------------------

    /// <summary>
    /// The single console the hero is showing, held so its live reachability keeps the panel current — the
    /// probe resolves after this returns, and a hero stuck on "Checking…" would be worse than a card doing it.
    /// </summary>
    private ConsoleCardViewModel? _heroConsole;

    private void AttachHero(ConsoleCardViewModel console)
    {
        _heroConsole = console;
        console.PropertyChanged += OnHeroChanged;

        // Rebuilt per console rather than reused: the menu's items close over this particular card.
        HeroCard.ContextFlyout = BuildConsoleFlyout(console);

        RenderHero(console.State);
    }

    private void DetachHero()
    {
        if (_heroConsole is { } previous)
        {
            previous.PropertyChanged -= OnHeroChanged;
            _heroConsole = null;
        }
    }

    private void OnHeroChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_heroConsole is { } console)
        {
            RenderHero(console.State);
        }
    }

    private void RenderHero(ConsoleCardState s)
    {
        HeroMark.Accent = AccentResources.Brush(s.Accent);
        HeroName.Text = s.DisplayName;
        HeroDetails.Text = s.Details;

        HeroLastPlayed.Text = s.LastConnectedLabel ?? string.Empty;
        HeroLastPlayed.Visibility = Vis(s.LastConnectedLabel is { Length: > 0 });

        HeroStatus.Text = s.StatusLabel;
        HeroStatusDot.Fill = ThemeBrush.Lookup(s.StatusTone switch
        {
            StatusTone.Positive => "SystemFillColorSuccessBrush",
            StatusTone.Caution => "SystemFillColorCautionBrush",
            StatusTone.Neutral => "TextFillColorDisabledBrush",
            _ => "TextFillColorTertiaryBrush",
        });

        HeroPlayLabel.Text = s.PrimaryActionLabel;
        // Escaped rather than pasted, per this file's own rule: a private-use codepoint sitting raw in a
        // C# string is invisible in a diff and quietly mangled by anything that re-encodes the file.
        // E7E8 = PowerButton, E768 = Play.
        HeroPlayIcon.Glyph = s.ActionGlyph == ActionGlyph.Wake ? "" : "";

        // Disabled rather than hidden: an unreachable console usually just needs switching on, and the button
        // vanishing would read as "this console is broken" rather than "it is not answering right now".
        HeroPlayButton.IsEnabled = s.CanConnect;
    }

    private void OnHeroPlayClick(object sender, RoutedEventArgs e)
    {
        if (_heroConsole is { } console)
        {
            Connect(console);
        }
    }

    private void OnHeroOverflowClick(object sender, RoutedEventArgs e)
    {
        if (_heroConsole is { } console)
        {
            BuildConsoleFlyout(console).ShowAt(HeroOverflowButton);
        }
    }

    private void OnHeroContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (_heroConsole is not { } console)
        {
            return;
        }

        MenuFlyout flyout = BuildConsoleFlyout(console);

        if (args.TryGetPosition(HeroCard, out Windows.Foundation.Point point))
        {
            flyout.ShowAt(HeroCard, new FlyoutShowOptions { Position = point });
        }
        else
        {
            flyout.ShowAt(HeroCard);
        }

        args.Handled = true;
    }

    // ---- responsive columns ----------------------------------------------------------------------

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e) => ApplyColumnCount();

    /// <summary>
    /// Cap the card row on the standard Windows breakpoints, in effective pixels — under 640 one column,
    /// 640–1007 two, wider than that as many as fit.
    ///
    /// <para>
    /// <c>ItemsWrapGrid</c> would otherwise fit whatever the arithmetic allows: at 292px cells a 639px window
    /// takes two columns, which on a phone-width or split-screen window leaves cards narrower than their own
    /// content wants. The breakpoints exist so a narrow window gets one readable card rather than two cramped
    /// ones.
    /// </para>
    /// </summary>
    private void ApplyColumnCount()
    {
        if (ConsoleGrid.ItemsPanelRoot is not ItemsWrapGrid panel)
        {
            return;
        }

        double width = ActualWidth;
        panel.MaximumRowsOrColumns = width switch
        {
            > 0 and < 640 => 1,
            >= 640 and < 1008 => 2,

            // -1 is "as many as fit", which is the right answer once there is room for three.
            _ => -1,
        };
    }

    private void CancelProbes()
    {
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        _probeCts = null;
    }

    // ----------------------------------------------------------------------------------------------------
    // Actions
    // ----------------------------------------------------------------------------------------------------

    private void OnAddConsoleClick(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(AddConsolePage));

    /// <summary>
    /// The whole card is the action. Which action depends on what the item is — a console connects, the ghost
    /// tile opens the add flow.
    /// </summary>
    private void OnConsoleCardClick(object sender, ItemClickEventArgs e)
    {
        switch (e.ClickedItem)
        {
            case AddConsolePlaceholder:
                Frame.Navigate(typeof(AddConsolePage));
                break;
            case ConsoleCardViewModel item:
                Connect(item);
                break;
        }
    }

    private void Connect(ConsoleCardViewModel item)
    {
        // Remember when, so the card can say "played 2 hours ago" next time. Stamped on the attempt rather
        // than on a successful stream: this is a recency hint for ordering the user's attention, not a record
        // of sessions, and a failed connect is still the console they were last reaching for.
        var stamped = item.Console with { LastConnectedUtc = DateTimeOffset.UtcNow };
        _store.Upsert(stamped);
        item.Update(stamped);

        PrepareConnectAnimation(item);

        // The stream is a window-level layer above the navigation chrome, not a page inside it — see
        // MainWindow.ShowStream. SessionPage then owns a SessionController for the whole lifecycle.
        _services.Shell.ShowStream(stamped);
    }

    /// <summary>
    /// Hand the card off to the stream page, so starting a session carries the eye across instead of cutting
    /// to black. Best-effort in every direction: if the container is not realised, or the animation is not
    /// picked up by the other page, navigation happens exactly as it did before.
    /// </summary>
    private void PrepareConnectAnimation(ConsoleCardViewModel item)
    {
        if (!AppMotion.Enabled)
        {
            return;
        }

        try
        {
            // Whichever layout is showing owns the element the eye is travelling from. On the hero there is no
            // grid container to ask for, so the card itself is the source — without this, the one-console
            // layout was the only path that cut to black.
            UIElement? source = HeroPanel.Visibility == Visibility.Visible
                ? HeroCard
                : ConsoleGrid.ContainerFromItem(item) as GridViewItem;

            if (source is not null)
            {
                ConnectedAnimationService.GetForCurrentView()
                    .PrepareToAnimate(AppMotion.ConnectAnimationKey, source);
            }
        }
        catch (Exception)
        {
            // Purely decorative — never let it stand between the user and their game.
        }
    }

    /// <summary>
    /// The card's secondary actions. Built here rather than in markup so the overflow button and the
    /// right-click / menu-key path share one definition \u2014 two copies would drift the moment either grew an
    /// entry.
    /// </summary>
    private MenuFlyout BuildConsoleFlyout(ConsoleCardViewModel item)
    {
        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };

        // Glyphs escaped rather than pasted: a private-use codepoint sitting raw in a C# string is invisible
        // in a diff and quietly mangled by anything that re-encodes the file.
        var rename = new MenuFlyoutItem { Text = "Rename", Icon = new FontIcon { Glyph = "\uE8AC" } };
        rename.Click += async (_, _) => await RenameAsync(item);

        var details = new MenuFlyoutItem { Text = "Details", Icon = new FontIcon { Glyph = "\uE946" } };
        details.Click += async (_, _) => await ShowDetailsAsync(item);

        var remove = new MenuFlyoutItem { Text = "Remove", Icon = new FontIcon { Glyph = "\uE74D" } };
        remove.Click += async (_, _) => await RemoveAsync(item);

        flyout.Items.Add(rename);
        flyout.Items.Add(details);
        // IsTabStop=false, or directional focus stops on it: a separator is decoration and activating it does
        // nothing, so a pad user gets a dead step between Details and Remove.
        flyout.Items.Add(new MenuFlyoutSeparator { IsTabStop = false });
        flyout.Items.Add(remove);

        return flyout;
    }

    private void OnConsoleOverflowClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ConsoleCardViewModel item } button)
        {
            BuildConsoleFlyout(item).ShowAt(button);
        }
    }

    /// <summary>
    /// Right-click, the keyboard menu key and the pad's menu button all arrive here, so reaching the overflow
    /// does not require aiming a pointer at a 32-pixel button.
    /// </summary>
    private void OnCardContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ConsoleCardViewModel item } element)
        {
            return;
        }

        MenuFlyout flyout = BuildConsoleFlyout(item);

        // A context request carries a point only when a pointer raised it. From the keyboard or a pad it does
        // not, and the menu should then hang off the card itself rather than the window's origin.
        if (args.TryGetPosition(element, out Windows.Foundation.Point point))
        {
            flyout.ShowAt(element, new FlyoutShowOptions { Position = point });
        }
        else
        {
            flyout.ShowAt(element);
        }

        args.Handled = true;
    }

    // The card's hover/focus cue. Held as a flag on the item rather than by reaching into the template, so it
    // survives the markup being rearranged \u2014 see ConsoleCardViewModel.IsHighlighted.
    private void OnCardHighlight(object sender, RoutedEventArgs e) => SetHighlight(sender, true);

    private void OnCardUnhighlight(object sender, RoutedEventArgs e) => SetHighlight(sender, false);

    private void OnCardHighlight(object sender, PointerRoutedEventArgs e) => SetHighlight(sender, true);

    private void OnCardUnhighlight(object sender, PointerRoutedEventArgs e) => SetHighlight(sender, false);

    private static void SetHighlight(object sender, bool on)
    {
        if (sender is FrameworkElement { DataContext: ConsoleCardViewModel item })
        {
            item.IsHighlighted = on;
        }
    }

    /// <summary>
    /// Wire each realized container, which is where focus actually lands.
    ///
    /// <para>
    /// <b>The container, not the card inside it.</b> The template's Border hooks GotFocus, but a
    /// <see cref="GridView"/> focuses the <see cref="GridViewItem"/> that <em>wraps</em> the template — and
    /// routed events bubble upward, so a descendant never learns that its own ancestor was focused. Everything
    /// hung on the Border's focus therefore only ever fired for the mouse: the accent wash never lit for a pad,
    /// and the context menu attached there was invisible to a search that walks up from the focused element.
    /// The pad's North button found nothing to open, which is exactly how this was discovered.
    /// </para>
    ///
    /// <para>
    /// Containers are recycled, so both the flyout and the handlers are reassigned on every pass rather than
    /// set once — a container that arrives carrying the previous item's menu is worse than one carrying none.
    /// </para>
    /// </summary>
    private void OnPrepareConsoleContainer(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not GridViewItem container)
        {
            return;
        }

        // Detach first: recycling means this container may still carry the last item's subscriptions.
        container.GotFocus -= OnContainerFocus;
        container.LostFocus -= OnContainerBlur;

        if (args.Item is not ConsoleCardViewModel item)
        {
            // The add tile has no per-console actions.
            container.ContextFlyout = null;
            return;
        }

        container.ContextFlyout = BuildConsoleFlyout(item);
        container.GotFocus += OnContainerFocus;
        container.LostFocus += OnContainerBlur;
    }

    private static void OnContainerFocus(object sender, RoutedEventArgs e) => SetContainerHighlight(sender, true);

    private static void OnContainerBlur(object sender, RoutedEventArgs e) => SetContainerHighlight(sender, false);

    private static void SetContainerHighlight(object sender, bool on)
    {
        if (sender is GridViewItem { Content: ConsoleCardViewModel item })
        {
            item.IsHighlighted = on;
        }
    }

    private async Task RenameAsync(ConsoleCardViewModel item)
    {
        var box = new TextBox
        {
            Text = item.State.DisplayName,
            SelectionStart = 0,
            SelectionLength = item.State.DisplayName.Length,
            PlaceholderText = item.Console.ReportedName ?? item.Console.Name,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Rename console",
            Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 320,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Give this console a name you'll recognise — useful when you have more than one.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    box,
                    new TextBlock
                    {
                        Text = "Leave it empty to go back to the name the console reports.",
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            string entered = box.Text.Trim();
            var updated = item.Console with { Nickname = entered.Length == 0 ? null : entered };
            _store.Upsert(updated);
            item.Update(updated);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Couldn't rename that console", ex.Message);
        }
    }

    private async Task ShowDetailsAsync(ConsoleCardViewModel item)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
        AddDetail(panel, "Name", item.State.DisplayName);
        AddDetail(panel, "Console", item.Family.LongName);
        AddDetail(panel, "Address", item.Console.Host);
        AddDetail(panel, "Status", item.State.StatusLabel);

        // Everything below is only known for consoles paired since discovery started carrying it, so each is
        // shown only when there is something to show rather than as a row of blanks.
        if (item.Console.ReportedName is { Length: > 0 } reported && reported != item.State.DisplayName)
        {
            AddDetail(panel, "Reported name", reported);
        }

        if (item.Console.HostId is { Length: > 0 } hostId)
        {
            AddDetail(panel, "Host ID", hostId);
        }

        if (item.Console.SystemVersion is { Length: > 0 } version)
        {
            AddDetail(panel, "System version", version);
        }

        if (item.State.LastConnectedLabel is { } played)
        {
            AddDetail(panel, "Last played", played);
        }

        try
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = item.State.DisplayName,
                Content = panel,
                CloseButtonText = "Close",
            }.ShowAsync();
        }
        catch (Exception)
        {
            // Another dialog is already open.
        }
    }

    private static void AddDetail(StackPanel panel, string label, string value)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = new TextBlock
        {
            Text = label,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var text = new TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        Grid.SetColumn(text, 1);

        row.Children.Add(name);
        row.Children.Add(text);
        panel.Children.Add(row);
    }

    private async Task RemoveAsync(ConsoleCardViewModel item)
    {
        try
        {
            // Removing a pairing is destructive and not undoable — it discards the credential, so getting the
            // console back means re-entering a link code on the console itself. It previously happened
            // instantly on a single click, next to the Connect button.
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Remove this console?",
                Content = $"Ripcord will forget its pairing with {item.State.DisplayName}. To use it again you'll need "
                          + "to enter a new link code from the console.",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                _store.Remove(item.Console.Id);
                Refresh();
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Couldn't remove that console", ex.Message);
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
