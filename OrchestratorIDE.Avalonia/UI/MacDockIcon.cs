// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.InteropServices;

namespace OrchestratorIDE.UI;

/// <summary>
/// The TheOrc icon in the macOS Dock and Cmd-Tab switcher. Run from source, the app is the
/// <c>dotnet</c> host, so macOS shows .NET's generic icon; Avalonia doesn't map a window icon
/// to the Dock. This hands AppKit <c>Assets/TheOrc.icns</c> (every size from 16 to 1024 px,
/// built from the repo's icon art by <c>Assets/macos/make-icon.swift</c>) via
/// <c>[NSApp setApplicationIconImage:]</c>. A no-op off macOS. Must run on the main thread.
/// </summary>
internal static class MacDockIcon
{
    internal const string FileName = "TheOrc.icns";

    internal static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Assets", FileName);

    /// <summary>Sets the Dock icon. False (never an exception) when it couldn't.</summary>
    public static bool TrySet(string? path = null)
    {
        if (!OperatingSystem.IsMacOS())
            return false;
        path ??= DefaultPath;
        if (!File.Exists(path))
            return false;
        try
        {
            // alloc/init + release rather than autoreleased objects: this runs before
            // Avalonia's run loop (and its autorelease pool) starts.
            var nsPath = ObjC.Send(ObjC.Send(ObjC.Class("NSString"), ObjC.Sel("alloc")),
                                   ObjC.Sel("initWithUTF8String:"), path);
            if (nsPath == IntPtr.Zero)
                return false;
            var image = ObjC.Send(ObjC.Send(ObjC.Class("NSImage"), ObjC.Sel("alloc")),
                                  ObjC.Sel("initWithContentsOfFile:"), nsPath);
            ObjC.SendVoid(nsPath, ObjC.Sel("release"));
            if (image == IntPtr.Zero)
                return false;   // not a readable image

            var app = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication"));
            ObjC.SendVoid(app, ObjC.Sel("setApplicationIconImage:"), image);   // NSApp retains it
            ObjC.SendVoid(image, ObjC.Sel("release"));
            return app != IntPtr.Zero;
        }
        catch
        {
            return false;   // cosmetic: never take the app down over an icon
        }
    }

    private static class ObjC
    {
        private const string Lib = "/usr/lib/libobjc.A.dylib";

        [DllImport(Lib, EntryPoint = "objc_getClass")]
        public static extern IntPtr Class(string name);

        [DllImport(Lib, EntryPoint = "sel_registerName")]
        public static extern IntPtr Sel(string name);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr Send(IntPtr receiver, IntPtr selector,
                                         [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern void SendVoid(IntPtr receiver, IntPtr selector);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr arg);
    }
}
