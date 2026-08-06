using System;
using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Ripcord_App.Input;

/// <summary>
/// Keeps focus on the same <em>item</em> in a list that reorders under the user, rather than on the same
/// position.
///
/// <para>
/// <b>The specific failure this exists for.</b> The discovery list inserts sorted: consoles of the family the
/// user picked go above the rest. Consoles answer a broadcast whenever they feel like it, so one can arrive
/// seconds in and insert <em>above</em> whatever the user is currently sitting on. A list keeps focus on the
/// realized container, and the container at that position is now showing a different console — so the caret
/// silently slid onto a neighbour. Pressing Select then pairs with a console the user was not looking at,
/// which is about the worst outcome available on this page.
/// </para>
///
/// <para>
/// <b>The rule, in two halves, and the second half matters as much as the first.</b> Re-focus the container of
/// the item that had focus — never the index. And <b>never move focus onto a newly arrived item</b>: a result
/// landing under the user's thumb must not steal the press. Together they mean an arrival can change the list
/// around the user without ever changing what they are pointing at.
/// </para>
///
/// <para>
/// Anchoring is driven by the list's own focus events, which bubble from the item containers, so the anchor is
/// non-null exactly while focus is somewhere inside the list. That is also the guard for the second half of
/// the rule: with no anchor there was nothing focused here to preserve, and the anchor does nothing at all.
/// </para>
/// </summary>
public sealed class FocusAnchor : IDisposable
{
    private readonly ItemsControl _list;
    private readonly DispatcherQueue _dispatcher;

    private INotifyCollectionChanged? _observed;
    private object? _anchoredItem;

    public FocusAnchor(ItemsControl list, DispatcherQueue dispatcher)
    {
        _list = list ?? throw new ArgumentNullException(nameof(list));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        _list.GotFocus += OnListGotFocus;
        _list.LostFocus += OnListLostFocus;
    }

    /// <summary>
    /// Watch a collection. Separate from the constructor because a page sets <c>ItemsSource</c> after the list
    /// exists, and re-callable so a list that is re-pointed does not keep watching the old collection.
    /// </summary>
    public void Watch(INotifyCollectionChanged? items)
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnCollectionChanged;
        }

        _observed = items;

        if (_observed is not null)
        {
            _observed.CollectionChanged += OnCollectionChanged;
        }
    }

    public void Dispose()
    {
        _list.GotFocus -= OnListGotFocus;
        _list.LostFocus -= OnListLostFocus;
        Watch(null);
        _anchoredItem = null;
    }

    private void OnListGotFocus(object sender, RoutedEventArgs e)
        => _anchoredItem = ItemOf(e.OriginalSource as DependencyObject);

    private void OnListLostFocus(object sender, RoutedEventArgs e)
    {
        // LostFocus bubbles here for a move *within* the list too, and at that moment the new element does not
        // report focus yet — so the anchor cannot simply be cleared. Ask once the move has settled: if focus
        // has genuinely left the list, drop the anchor, which is what stops later arrivals pulling focus back
        // into a list the user has walked away from.
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_list.XamlRoot is null)
            {
                return;
            }

            object? focused = FocusManager.GetFocusedElement(_list.XamlRoot);

            if (focused is not DependencyObject element || ItemOf(element) is null)
            {
                _anchoredItem = null;
            }
        });
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_anchoredItem is null)
        {
            return;
        }

        // A reset clears the list outright — a rescan. The anchored item is gone rather than moved, so there is
        // nothing to restore and the ordinary seeding path should decide what happens next.
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _anchoredItem = null;
            return;
        }

        object item = _anchoredItem;

        // Containers are generated after the change, so the question can only be asked once layout has caught up.
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            // Focus left the list while this was queued: the user moved on, and dragging them back would be a
            // worse bug than the one being fixed.
            if (!ReferenceEquals(_anchoredItem, item) || _list.XamlRoot is null)
            {
                return;
            }

            if (Focusable(_list.ContainerFromItem(item)) is not Control target)
            {
                return;
            }

            // Already right — the common case, since a container only slides when something inserted above it.
            if (ReferenceEquals(FocusManager.GetFocusedElement(_list.XamlRoot), target))
            {
                return;
            }

            target.Focus(FocusState.Keyboard);
        });
    }

    /// <summary>The list item a focused element belongs to, or null when the element is not an item of this list.</summary>
    private object? ItemOf(DependencyObject? element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualParent(node))
        {
            if (_list.ItemFromContainer(node) is { } item)
            {
                return item;
            }

            if (ReferenceEquals(node, _list))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// The thing focus can actually land on for a container.
    ///
    /// <para>
    /// A plain <c>ItemsControl</c> wraps each item in a <c>ContentPresenter</c>, which is not focusable — what
    /// takes focus is the control the template puts inside it. (A <c>ListView</c> would hand back a focusable
    /// <c>ListViewItem</c> directly, so this looks redundant against the more familiar list and is not.)
    /// </para>
    /// </summary>
    private static Control? Focusable(DependencyObject? container)
        => container switch
        {
            null => null,
            Control { IsTabStop: true } control => control,
            _ => FocusManager.FindFirstFocusableElement(container) as Control,
        };

    private static DependencyObject? VisualParent(DependencyObject node)
        => Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
}
