// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OrchestratorIDE.UI.Converters;

/// <summary>
/// Converts a "#RRGGBB" hex string to a SolidColorBrush.
/// Used by the activity log DataTemplate to bind ActivityEvent.IconColor.
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex)
        {
            try { return new SolidColorBrush(Color.Parse(hex)); }
            catch { /* fall through */ }
        }
        return new SolidColorBrush(Color.Parse("#569CD6"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts HasFilePath bool to a foreground brush.
/// True → blue clickable hint; False → default muted text.
/// </summary>
public sealed class FilePathForegroundConverter : IValueConverter
{
    public static readonly FilePathForegroundConverter Instance = new();

    private static readonly IBrush BlueBrush   = new SolidColorBrush(Color.Parse("#9CDCFE"));
    private static readonly IBrush DefaultBrush = new SolidColorBrush(Color.Parse("#D4D4D4"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? BlueBrush : DefaultBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
