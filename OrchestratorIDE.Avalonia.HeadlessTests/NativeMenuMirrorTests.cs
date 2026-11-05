// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NUnit.Framework;
using OrchestratorIDE.UI;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// NativeMenuMirror copies the in-window menu into the macOS menu bar. The original MenuItems
/// stay the source of truth: clicks forward to them, and later changes (Open Recent refills,
/// Word Wrap checks, enable/disable) reach the native items.
/// </summary>
[TestFixture]
public class NativeMenuMirrorTests
{
    // NativeMenuItem exposes no public "click" trigger. The macOS menu exporter calls
    // INativeMenuItemExporterEventsImplBridge.RaiseClicked (an explicit, internal interface
    // implementation), so the test invokes that same hook.
    private static void Click(NativeMenuItem item)
    {
        var raise = typeof(NativeMenuItem)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(m => m.Name.EndsWith(".RaiseClicked", StringComparison.Ordinal)
                                  && m.GetParameters().Length == 0);
        Assert.That(raise, Is.Not.Null, "NativeMenuItem's RaiseClicked bridge not found in this Avalonia version");
        raise!.Invoke(item, null);
    }

    private static NativeMenuItem Top(NativeMenu menu, int i) => (NativeMenuItem)menu.Items[i];

    [AvaloniaTest]
    public void Build_mirrors_structure_and_strips_access_keys()
    {
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(new MenuItem { Header = "Open Folder…" });
        file.Items.Add(new Separator());
        file.Items.Add(new MenuItem { Header = "Exit" });
        var warm = new MenuItem { Header = "Warm Up" };
        warm.Items.Add(new MenuItem { Header = "Researcher" });
        file.Items.Add(warm);
        var menu = new Menu();
        menu.Items.Add(file);

        var native = NativeMenuMirror.Build(menu);

        var nativeFile = Top(native, 0);
        Assert.That(nativeFile.Header, Is.EqualTo("File"));
        Assert.That(nativeFile.Menu, Is.Not.Null);
        var items = nativeFile.Menu!.Items;
        Assert.That(items, Has.Count.EqualTo(4));
        Assert.That(((NativeMenuItem)items[0]).Header, Is.EqualTo("Open Folder…"));
        Assert.That(items[1], Is.InstanceOf<NativeMenuItemSeparator>());
        Assert.That(((NativeMenuItem)items[3]).Menu!.Items, Has.Count.EqualTo(1));
    }

    [AvaloniaTest]
    public void Click_forwards_to_the_original_item_and_its_handlers()
    {
        var clicks = 0;
        object? sender = null;
        var exit = new MenuItem { Header = "Exit", Tag = "exit" };
        exit.Click += (s, _) => { clicks++; sender = s; };
        var menu = new Menu();
        menu.Items.Add(exit);

        var native = NativeMenuMirror.Build(menu);
        Click(Top(native, 0));

        Assert.That(clicks, Is.EqualTo(1));
        Assert.That(sender, Is.SameAs(exit), "handlers that read sender.Tag (Warm Up) must still see the MenuItem");
    }

    [AvaloniaTest]
    public void Disabled_items_stay_disabled_and_do_not_fire()
    {
        var clicks = 0;
        var item = new MenuItem { Header = "Open Recent", IsEnabled = false };
        item.Click += (_, _) => clicks++;
        var menu = new Menu();
        menu.Items.Add(item);

        var native = NativeMenuMirror.Build(menu);
        Assert.That(Top(native, 0).IsEnabled, Is.False);
        Click(Top(native, 0));
        Assert.That(clicks, Is.Zero);

        item.IsEnabled = true;
        Assert.That(Top(native, 0).IsEnabled, Is.True);
    }

    [AvaloniaTest]
    public void Rebuilding_a_submenu_from_code_reaches_the_native_menu()
    {
        // Same shape as MainWindow.RebuildRecentMenu: starts empty, then filled and refilled.
        var recent = new MenuItem { Header = "Open Recent", IsEnabled = false };
        var menu = new Menu();
        menu.Items.Add(recent);
        var native = NativeMenuMirror.Build(menu);
        Assert.That(Top(native, 0).Menu, Is.Null);

        var opened = "";
        var project = new MenuItem { Header = "TheOrc" };
        project.Click += (_, _) => opened = "TheOrc";
        recent.Items.Add(project);
        recent.IsEnabled = true;

        var submenu = Top(native, 0).Menu;
        Assert.That(submenu, Is.Not.Null);
        Assert.That(submenu!.Items, Has.Count.EqualTo(1));
        Click((NativeMenuItem)submenu.Items[0]);
        Assert.That(opened, Is.EqualTo("TheOrc"));

        recent.Items.Clear();
        Assert.That(Top(native, 0).Menu, Is.Null);
    }

    [AvaloniaTest]
    public void Checking_an_item_from_code_shows_a_checkmark()
    {
        var wrap = new MenuItem { Header = "Word Wrap" };
        var menu = new Menu();
        menu.Items.Add(wrap);
        var native = NativeMenuMirror.Build(menu);
        Assert.That(Top(native, 0).IsChecked, Is.False);

        wrap.IsChecked = true;
        Assert.That(Top(native, 0).ToggleType, Is.EqualTo(MenuItemToggleType.CheckBox));
        Assert.That(Top(native, 0).IsChecked, Is.True);

        wrap.IsChecked = false;
        Assert.That(Top(native, 0).IsChecked, Is.False);
    }

    [TestCase("_File", "File")]
    [TestCase("Open Folder…", "Open Folder…")]
    [TestCase("snake__case", "snake_case")]
    [TestCase("", "")]
    public void StripAccessKey_removes_mnemonic_underscores(string header, string expected) =>
        Assert.That(NativeMenuMirror.StripAccessKey(header), Is.EqualTo(expected));
}
