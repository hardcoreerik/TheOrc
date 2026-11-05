// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using NUnit.Framework;
using OrchestratorIDE.UI;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// The macOS app icon: a valid .icns with every size the Dock, Finder and Cmd-Tab use, copied
/// into the build on macOS only. Setting it on NSApp is checked in the running app, not here:
/// doing that from the test host would put a stray icon in the Dock.
/// </summary>
[TestFixture]
public class MacDockIconTests
{
    private static string RepoIcns()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Assets", "macos", MacDockIcon.FileName);
            if (File.Exists(candidate))
                return candidate;
        }
        Assert.Fail("Assets/macos/TheOrc.icns not found above the test output folder.");
        return "";
    }

    /// <summary>Entry type codes in an .icns file (big-endian "icns", length, then entries).</summary>
    private static List<string> IcnsEntries(byte[] bytes)
    {
        Assert.That(Encoding.ASCII.GetString(bytes, 0, 4), Is.EqualTo("icns"));
        Assert.That(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)), Is.EqualTo((uint)bytes.Length));
        var types = new List<string>();
        for (var at = 8; at + 8 <= bytes.Length;)
        {
            types.Add(Encoding.ASCII.GetString(bytes, at, 4));
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 4));
            Assert.That(len, Is.GreaterThan(8), "entry length");
            at += len;
        }
        return types;
    }

    [Test]
    public void Icns_holds_every_size_from_16_to_1024_px()
    {
        var types = IcnsEntries(File.ReadAllBytes(RepoIcns()));
        // 16, 32, 128, 256, 512, 1024 px and the @2x variants of 16/32/128/256.
        Assert.That(types, Is.SupersetOf(new[]
            { "ic04", "ic05", "ic07", "ic08", "ic09", "ic10", "ic11", "ic12", "ic13", "ic14" }));
    }

    [Test]
    public void Icon_is_in_the_build_output_on_macos_only()
    {
        // The headless test output receives the app project's content files.
        var copied = File.Exists(MacDockIcon.DefaultPath);
        Assert.That(copied, Is.EqualTo(OperatingSystem.IsMacOS()),
            OperatingSystem.IsMacOS() ? "the Dock icon must ship with the macOS build"
                                      : "Windows/Linux builds are unchanged");
    }

    [Test]
    public void TrySet_is_a_safe_no_op_for_a_missing_file_and_off_macos()
    {
        Assert.That(MacDockIcon.TrySet("/definitely/not/here/" + Guid.NewGuid() + ".icns"), Is.False);
        if (!OperatingSystem.IsMacOS())
            Assert.That(MacDockIcon.TrySet(RepoIcns()), Is.False);
    }
}
