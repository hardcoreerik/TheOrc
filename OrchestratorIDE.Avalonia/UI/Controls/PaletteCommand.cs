// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Media;

namespace OrchestratorIDE.UI.Controls;

public class PaletteCommand
{
    public string   Id       { get; set; } = "";
    public string   Label    { get; set; } = "";
    public string   Detail   { get; set; } = "";
    public string   Icon     { get; set; } = "·";
    public string   Shortcut { get; set; } = "";
    public string[] Keywords { get; set; } = [];
    public int      SortOrder { get; set; } = 100;

    public IBrush IconColor => Icon switch
    {
        "⚡" => new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)),
        "📁" => new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)),
        "⚙"  => new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00)),
        "⬡"  => new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA)),
        "▶"  => new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)),
        "●"  => new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47)),
        _    => new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85)),
    };
}
