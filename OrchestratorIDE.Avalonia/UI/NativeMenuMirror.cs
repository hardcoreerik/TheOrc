// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OrchestratorIDE.UI;

/// <summary>
/// macOS: mirrors the in-window <see cref="Menu"/> into the system menu bar
/// (<see cref="NativeMenu"/>), so File/Edit/View/... live at the top of the screen like every
/// other Mac app instead of inside the window.
///
/// The in-window menu stays the single source of truth: every native item forwards its click
/// to the original <see cref="MenuItem"/> (so the existing XAML <c>Click</c> handlers and
/// code-added handlers such as Open Recent's run unchanged), and header, enabled, visible,
/// checked and child-item changes made to the original are copied across live. Nothing here
/// runs on Windows or Linux — MainWindow only calls it on macOS.
/// </summary>
internal static class NativeMenuMirror
{
    public static NativeMenu Build(Menu menu)
    {
        var native = new NativeMenu();
        MirrorItems(menu.Items, native);
        return native;
    }

    /// <summary>
    /// Fills <paramref name="target"/> from <paramref name="items"/> and keeps it in sync when
    /// the source collection changes (e.g. MainWindow.RebuildRecentMenu clears and refills
    /// Open Recent).
    /// </summary>
    private static void MirrorItems(IEnumerable items, NativeMenu target)
    {
        void Refill()
        {
            target.Items.Clear();
            foreach (var item in items)
                if (Convert(item) is { } converted)
                    target.Items.Add(converted);
        }

        Refill();
        if (items is INotifyCollectionChanged observable)
            observable.CollectionChanged += (_, _) => Refill();
    }

    internal static NativeMenuItemBase? Convert(object? item) => item switch
    {
        Separator => new NativeMenuItemSeparator(),
        MenuItem menuItem => ConvertItem(menuItem),
        _ => null,
    };

    private static NativeMenuItem ConvertItem(MenuItem source)
    {
        var native = new NativeMenuItem();

        void Sync()
        {
            native.Header    = StripAccessKey(source.Header?.ToString());
            native.IsEnabled = source.IsEnabled;
            native.IsVisible = source.IsVisible;
            native.ToolTip   = ToolTip.GetTip(source) as string;
            native.Gesture   = source.InputGesture;

            // A plain MenuItem whose IsChecked is driven from code (View > Word Wrap) has
            // ToggleType None; show it as a checkbox once it is checked, so the tick appears.
            var toggle = source.ToggleType;
            native.ToggleType = toggle == MenuItemToggleType.Radio ? MenuItemToggleType.Radio
                              : toggle == MenuItemToggleType.CheckBox || source.IsChecked ? MenuItemToggleType.CheckBox
                              : native.ToggleType;
            native.IsChecked = source.IsChecked;
        }

        Sync();
        source.PropertyChanged += (_, e) =>
        {
            if (e.Property == MenuItem.HeaderProperty ||
                e.Property == MenuItem.IsEnabledProperty ||
                e.Property == MenuItem.IsVisibleProperty ||
                e.Property == MenuItem.IsCheckedProperty ||
                e.Property == MenuItem.ToggleTypeProperty ||
                e.Property == MenuItem.InputGestureProperty ||
                e.Property == ToolTip.TipProperty)
                Sync();
        };

        // Submenu while the source has children (Warm Up; Open Recent once it has entries),
        // otherwise a clickable leaf that forwards to the original item.
        void SyncSubmenu()
        {
            if (source.ItemCount > 0)
            {
                if (native.Menu is null)
                {
                    var submenu = new NativeMenu();
                    MirrorItems(source.Items, submenu);
                    native.Menu = submenu;
                }
            }
            else
            {
                native.Menu = null;
            }
        }

        SyncSubmenu();
        if (source.Items is INotifyCollectionChanged children)
            children.CollectionChanged += (_, _) => SyncSubmenu();

        native.Click += (_, _) =>
        {
            if (source.IsEffectivelyEnabled)
                source.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        };

        return native;
    }

    /// <summary>"_File" → "File"; "__" stays a literal underscore (Avalonia's access-key rule).</summary>
    internal static string StripAccessKey(string? header)
    {
        if (string.IsNullOrEmpty(header))
            return "";
        const string placeholder = "\u0001";
        return header.Replace("__", placeholder).Replace("_", "").Replace(placeholder, "_");
    }
}
