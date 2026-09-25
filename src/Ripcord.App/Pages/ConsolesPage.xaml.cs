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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Ripcord.Core.Consoles;
using Ripcord.Core.Launch;
using Ripcord.Presentation;
using Ripcord.Presentation.Consoles;
using Ripcord_App.Input;
using Ripcord_App.Accents;
using Ripcord_App.Converters;
using Ripcord_App.Services;

namespace Ripcord_App.Pages;

public sealed partial class ConsolesPage : Page, IInitialFocusTarget
{
    /// <summary>
    /// Whatever this page currently says is the point of it. Open the window, press A, playing.
    ///
    /// <para>
    /// The grid, not a specific container: focusing a <see cref="GridView"/> hands focus to its own first (or
    /// last-focused) item, which survives the list being rebuilt underneath. That now covers the one-console
    /// case too, which used to be a separate panel with a separate focus target — it is a hero-sized cell in
    /// the same grid, so there is one answer here instead of two.
    /// </para>
    ///
    /// <para>
    /// With nothing paired it is null and the shell falls back to tree order: an empty install has no console
    /// to offer and the add button is genuinely the point.
    /// </para>
    /// </summary>
    public Control? InitialFocus
        => ConsoleGrid.Visibility == Visibility.Visible ? ConsoleGrid : null;

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

    // Guards the hero's focus-on-load against firing again when containers are recycled - a scroll or a
    // refresh re-realises them, and pulling focus back to the card mid-interaction would be worse than never
    // giving it. Reset by Refresh, which is the only place the layout can change underneath it.
    private bool _heroFocusTaken;

    // The layout the cards were last composed for, so container preparation can size the panel without
    // recomputing it from a width that may have moved since.
    private CardLayout _layout;

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
        Unloaded += (_, _) => CancelProbes();
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
    /// Two surfaces now, not three. Nothing paired → the first-run explanation. Anything else → the grid, at
    /// whatever density <see cref="CardMetrics"/> returns for the viewport and the count. The one-console
    /// "hero" is that grid holding a single hero-sized cell, which is what removed ~115 lines of code-behind
    /// and, with them, the drift between two copies of the same card.
    /// </para>
    /// </summary>
    private void Refresh()
    {
        CancelProbes();
        _heroFocusTaken = false;

        List<ConsoleCardViewModel> consoles = _store.Load().Select(_services.CreateConsoleCard).ToList();

        _items.Clear();

        foreach (ConsoleCardViewModel item in consoles)
        {
            _items.Add(item);
        }

        CardLayout layout = ApplyCardLayout(consoles.Count);

        // The ghost tile belongs in the collection only when the grid is dense enough for it to read as "one
        // more of these". Beside a single hero-sized card it would look like half the page's purpose, so that
        // layout offers the quiet link below instead.
        if (layout.ShowAddTile)
        {
            _items.Add(AddConsolePlaceholder.Instance);
        }

        ConsoleGrid.Visibility = Vis(consoles.Count > 0);
        EmptyState.Visibility = Vis(consoles.Count == 0);
        HeroAddLink.Visibility = Vis(consoles.Count > 0 && !layout.ShowAddTile);

        // The subtitle instructs someone to pick from several. With one console there is nothing to pick.
        SubtitleText.Visibility = Vis(consoles.Count > 1);

        TryLaunchDirectly(consoles);

        // Kept in step with whatever is paired. Fire-and-forget because nothing on this page waits on it and
        // a jump list that failed to rebuild is not worth a word to anyone - see PlayJumpList.
        _ = PlayJumpList.RefreshAsync(consoles.Select(c => c.Console).ToList());

        if (consoles.Count > 0)
        {
            // Consume the rest intent for this pass only; a later plain Refresh must not re-arm the watch.
            string? restHost = _restRequestedHost;
            _restRequestedHost = null;

            _probeCts = new CancellationTokenSource();
            _ = _reachability.RefreshAsync(consoles, restHost, _probeCts.Token);
        }
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    // Consumed once. Coming back from a stream runs Refresh again, and a launch argument that fired every
    // time would trap the player in a loop they cannot leave without killing the process.
    private bool _launchConsumed;

    /// <summary>
    /// Act on <c>--play</c> or <c>--play-last</c>, if this launch carried one.
    ///
    /// <para>
    /// From here rather than from startup because this is the first moment the paired-console list exists,
    /// and the argument names a console rather than an address. A name that matches nothing falls through to
    /// the list, which is the honest outcome: the shortcut is stale, and showing every console is what the
    /// person was reaching for anyway.
    /// </para>
    /// </summary>
    private void TryLaunchDirectly(List<ConsoleCardViewModel> consoles)
    {
        if (_launchConsumed || consoles.Count == 0)
        {
            return;
        }

        _launchConsumed = true;

        ConsoleCardViewModel? target = App.Launch.Action switch
        {
            LaunchAction.Play => consoles.FirstOrDefault(
                c => App.Launch.Matches(c.Console.DisplayName, c.Console.Id)),

            // Most recently reached for, which is what "last" means to the person who typed it - the stamp
            // is written on the attempt, not on a successful stream.
            LaunchAction.PlayLast => consoles
                .Where(c => c.Console.LastConnectedUtc is not null)
                .OrderByDescending(c => c.Console.LastConnectedUtc)
                .FirstOrDefault(),

            _ => null,
        };

        if (target is null)
        {
            return;
        }

        // Enqueued rather than called. Refresh runs from the page's Loaded, and navigating away from a page
        // that is still loading leaves the frame in a state where the navigation is dropped - the console
        // was stamped as played and nothing happened, which is the worst of both. By the time the queue
        // drains the page has finished loading and the navigation takes.
        DispatcherQueue.TryEnqueue(() => Connect(target));
    }

    // ---- responsive columns ----------------------------------------------------------------------

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyCardLayout(_items.OfType<ConsoleCardViewModel>().Count());

    /// <summary>
    /// Size the grid's cells and cap its columns, from <see cref="CardMetrics"/>.
    ///
    /// <para>
    /// The arithmetic is not here on purpose. Breakpoints, cell sizes and the column cap are a rule that can
    /// be wrong on a display nobody in the room owns, and the previous version of it — three literals in a
    /// switch — could only be checked by resizing a window and looking. <c>CardMetrics</c> is pure and has
    /// tests; this method's whole job is to hand it a width and apply the answer.
    /// </para>
    ///
    /// <para>
    /// Returns the layout so the caller can also ask it about the add tile, rather than computing it twice
    /// from the same two inputs and eventually disagreeing with itself.
    /// </para>
    /// </summary>
    private CardLayout ApplyCardLayout(int consoleCount)
    {
        CardLayout layout = CardMetrics.For(ActualWidth, consoleCount);
        _layout = layout;

        foreach (ConsoleCardViewModel card in _items.OfType<ConsoleCardViewModel>())
        {
            card.Density = layout.Density;
        }

        SizePanel();

        // A single hero-sized card is the page; anything denser is a list and reads from the top-left.
        bool hero = layout.Density == CardDensity.Hero;
        ConsoleGrid.HorizontalAlignment = hero ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        ConsoleGrid.VerticalAlignment = hero ? VerticalAlignment.Center : VerticalAlignment.Stretch;

        return layout;
    }

    /// <summary>
    /// Push the current cell size onto the wrapping panel, if it exists yet.
    ///
    /// <para>
    /// <b>It usually does not, at the moment you would expect it to.</b> <c>ItemsPanelRoot</c> is null while
    /// the page is loading and still null in the grid's own <c>Loaded</c> - it is materialised from the
    /// <c>ItemsPanelTemplate</c> during the first measure pass. Setting the size before then goes nowhere and
    /// leaves the markup's own <c>ItemWidth</c> standing, which put a hero-density CARD inside a
    /// grid-density CELL and wrapped the console's name to one letter per line.
    /// </para>
    ///
    /// <para>
    /// So it is called again from container preparation, which cannot run before the panel exists. Guarded
    /// against writing values that already match, because assigning these invalidates layout and this is
    /// reached from inside a layout pass.
    /// </para>
    /// </summary>
    private void SizePanel()
    {
        if (ConsoleGrid.ItemsPanelRoot is not ItemsWrapGrid panel)
        {
            return;
        }

        if (panel.ItemWidth != _layout.CellWidth)
        {
            panel.ItemWidth = _layout.CellWidth;
        }

        if (panel.ItemHeight != _layout.CellHeight)
        {
            panel.ItemHeight = _layout.CellHeight;
        }

        if (panel.MaximumRowsOrColumns != _layout.MaxColumns)
        {
            panel.MaximumRowsOrColumns = _layout.MaxColumns;
        }
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
            // Always the container now. This used to branch on which layout was showing, because the hero
            // card sat outside any items control and had no container to ask for - so the one-console path
            // was the only one that cut to black when the branch was wrong. Every card is a GridViewItem.
            UIElement? source = ConsoleGrid.ContainerFromItem(item) as GridViewItem;

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
    /// <summary>
    /// Write a desktop shortcut that launches straight into this console, and say whether it worked.
    ///
    /// <para>
    /// Told rather than assumed: the desktop can be redirected somewhere read-only or onto a share that is
    /// not there, and a menu item that silently does nothing is worse than one that says it could not.
    /// </para>
    /// </summary>
    private void CreateShortcut(ConsoleCardViewModel item)
    {
        string? path = PlayShortcut.WriteToDesktop(item.Console.DisplayName);

        ShortcutBar.Message = path is null ? ConsoleCardCopy.ShortcutFailed : ConsoleCardCopy.ShortcutMade;
        ShortcutBar.Severity = path is null
            ? InfoBarSeverity.Warning
            : InfoBarSeverity.Success;
        ShortcutBar.IsOpen = true;
    }

    private MenuFlyout BuildConsoleFlyout(ConsoleCardViewModel item)
    {
        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };

        // Glyphs escaped rather than pasted: a private-use codepoint sitting raw in a C# string is invisible
        // in a diff and quietly mangled by anything that re-encodes the file.
        var rename = new MenuFlyoutItem { Text = ConsoleCardCopy.MenuRename, Icon = new FontIcon { Glyph = "\uE8AC" } };
        rename.Click += async (_, _) => await RenameAsync(item);

        var details = new MenuFlyoutItem { Text = ConsoleCardCopy.MenuDetails, Icon = new FontIcon { Glyph = "\uE946" } };
        details.Click += async (_, _) => await ShowDetailsAsync(item);

        var remove = new MenuFlyoutItem { Text = ConsoleCardCopy.MenuRemove, Icon = new FontIcon { Glyph = "\uE74D" } };
        remove.Click += async (_, _) => await RemoveAsync(item);

        // The artefact somebody adds to Steam, to a handheld launcher, or pins to their taskbar. It is the
        // half of "launch at a console" that works without package identity - a jump list needs it and the
        // zip has none, and the zip is how most people will run this.
        var shortcut = new MenuFlyoutItem
        {
            Text = ConsoleCardCopy.MenuShortcut,
            Icon = new FontIcon { Glyph = "" },
        };
        shortcut.Click += (_, _) => CreateShortcut(item);

        flyout.Items.Add(rename);
        flyout.Items.Add(details);
        flyout.Items.Add(shortcut);
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
    private void OnCardHighlight(object sender, PointerRoutedEventArgs e) => SetHighlight(sender, true);

    private void OnCardUnhighlight(object sender, PointerRoutedEventArgs e) => SetHighlight(sender, false);

    /// <summary>
    /// The pointer wash, and only the pointer. Focus is the container's ring - see the container style - and
    /// the two are deliberately different kinds of mark: they were one shared wash at two opacities until
    /// 2026-09-20, which left a pad user unable to tell where focus was.
    /// </summary>
    private static void SetHighlight(object sender, bool on)
    {
        if (sender is FrameworkElement { DataContext: ConsoleCardViewModel item })
        {
            item.IsHighlighted = on;

            // Hover is reported separately from the wash's hover-or-focus, because the wedge answers this
            // one and must not answer focus. Cleared on exit, which also clears any press left behind by a
            // pointer that left the card mid-press.
            item.IsPointerOver = on;

            if (!on)
            {
                item.IsPressed = false;
            }
        }
    }

    private void OnCardDown(object sender, PointerRoutedEventArgs e) => SetPressed(sender, true);

    /// <summary>
    /// Release, cancel, and capture-lost all end a press.
    ///
    /// <para>
    /// Capture-lost is the one that is easy to omit and the one that strands a card lit: a press that turns
    /// into a scroll or is interrupted by a flyout never raises Released, and the wedge would stay at its
    /// pressed strength until the pointer happened to leave.
    /// </para>
    /// </summary>
    private void OnCardUp(object sender, PointerRoutedEventArgs e) => SetPressed(sender, false);

    /// <summary>How far a pressed card settles. Small on purpose: felt rather than watched.</summary>
    private const double PressScaleFactor = 0.985;

    private static void SetPressed(object sender, bool down)
    {
        if (sender is not FrameworkElement { DataContext: ConsoleCardViewModel item } element)
        {
            return;
        }

        item.IsPressed = down;

        // The scale is the front end's alone - it is a WinUI transform, and the portable layer holds no UI
        // types. Skipped entirely when motion is off rather than run at zero duration, so a player who asked
        // Windows for less motion gets none rather than an instant jump.
        if (!AppMotion.Enabled || element.FindName("PressScale") is not ScaleTransform scale)
        {
            return;
        }

        double target = down ? PressScaleFactor : 1.0;
        scale.ScaleX = target;
        scale.ScaleY = target;
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

        // The first moment the wrapping panel is guaranteed to exist. See SizePanel.
        SizePanel();

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

        // Open the window, press A, playing - the promise the one-console layout exists to keep.
        //
        // Done from here rather than from InitialFocus because at hero density the answer is a container that
        // does not exist yet when the shell asks: the GridView realises its items after the page's own Loaded,
        // so focusing the grid at that point lands on nothing and the first press goes nowhere. This fires as
        // the container is realised, which is the first moment there is something to focus.
        //
        // Only at hero density, and only the first card. In a dense grid the shell's own answer is correct -
        // focusing the GridView hands focus to whichever item it last had, which is what someone returning to
        // the page expects, and stealing that to the first card would undo it.
        if (item.Density == CardDensity.Hero && args.ItemIndex == 0 && !_heroFocusTaken)
        {
            _heroFocusTaken = true;
            _ = container.Focus(FocusState.Programmatic);
        }
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
            Title = ConsoleCardCopy.RenameTitle,
            Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 320,
                Children =
                {
                    new TextBlock
                    {
                        Text = ConsoleCardCopy.RenameExplanation,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    box,
                    new TextBlock
                    {
                        Text = ConsoleCardCopy.RenameHint,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
            PrimaryButtonText = ConsoleCardCopy.RenameSave,
            CloseButtonText = ConsoleCardCopy.Cancel,
            DefaultButton = ContentDialogButton.Primary,
        };

        try
        {
            if (await ModalHost.ShowAsync(dialog) != ContentDialogResult.Primary)
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
            await ShowErrorAsync(ConsoleCardCopy.RenameFailed, ex.Message);
        }
    }

    private async Task ShowDetailsAsync(ConsoleCardViewModel item)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 320 };

        // Which rows exist is a decision about what the surface says - most of these facts are only known
        // for consoles paired since discovery started carrying them - so it is made in the presentation
        // layer and tested there. This loop is the whole of the page's part in it.
        foreach (ConsoleDetail detail in ConsoleCardCopy.Details(item))
        {
            AddDetail(panel, detail.Label, detail.Value);
        }

        try
        {
            await ModalHost.ShowAsync(new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = item.State.DisplayName,
                Content = panel,
                CloseButtonText = ConsoleCardCopy.DetailsClose,
            });
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
                Title = ConsoleCardCopy.RemoveTitle,
                Content = ConsoleCardCopy.RemovePrompt(item.State.DisplayName),
                PrimaryButtonText = ConsoleCardCopy.RemoveConfirm,
                CloseButtonText = ConsoleCardCopy.Cancel,
                DefaultButton = ContentDialogButton.Close,
            };

            if (await ModalHost.ShowAsync(confirm) == ContentDialogResult.Primary)
            {
                _store.Remove(item.Console.Id);
                Refresh();
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ConsoleCardCopy.RemoveFailed, ex.Message);
        }
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        try
        {
            await ModalHost.ShowAsync(new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = message,
                CloseButtonText = ConsoleCardCopy.Ok,
            });
        }
        catch (Exception)
        {
            // Another dialog is already open; the original error is more important than reporting this.
        }
    }
}
