// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Builds the macOS app icon from the repo's existing icon art (Assets/icon.png): the same orc,
// octagon and circuit traces, re-framed on Apple's icon grid (an 824 px rounded square on a
// 1024 px transparent canvas, with the standard drop shadow). The source has a tighter corner
// than macOS and no transparency, so its interior art is kept and its neon rim is redrawn
// along the macOS shape with the same colors.
//
// Usage (macOS, no dependencies):  swift Assets/macos/make-icon.swift
// Writes Assets/macos/TheOrc.icns (via iconutil; every size from 16 to 1024 px).
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

let here = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
let sourceURL = here.appendingPathComponent("../icon.png").standardizedFileURL

// ── Apple's icon grid (macOS 11+) ────────────────────────────────────────────────────────
let canvas: CGFloat = 1024
let body = CGRect(x: 100, y: 100, width: 824, height: 824)

/// macOS's continuous-corner icon shape, as a superellipse |x|^n + |y|^n = 1.
func squircle(_ r: CGRect, n: CGFloat = 5) -> CGPath {
    let p = CGMutablePath()
    let steps = 1440
    for i in 0...steps {
        let t = CGFloat(i) / CGFloat(steps) * 2 * .pi
        let c = cos(t), s = sin(t)
        let x = pow(abs(c), 2 / n) * (c < 0 ? -1 : 1)
        let y = pow(abs(s), 2 / n) * (s < 0 ? -1 : 1)
        let pt = CGPoint(x: r.midX + x * r.width / 2, y: r.midY + y * r.height / 2)
        i == 0 ? p.move(to: pt) : p.addLine(to: pt)
    }
    p.closeSubpath()
    return p
}

func color(_ hex: UInt32, _ a: CGFloat = 1) -> CGColor {
    CGColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: a)
}

// ── Source art ───────────────────────────────────────────────────────────────────────────
guard let src = CGImageSourceCreateWithURL(sourceURL as CFURL, nil),
      let art = CGImageSourceCreateImageAtIndex(src, 0, nil) else {
    fatalError("Can't read \(sourceURL.path)")
}
// In icon.png (1254 px) the rim runs from 22 to 62 px in on every side; the art starts at 63.
let srcSize = CGFloat(art.width)
let srcInset: CGFloat = 63
let srcRim: CGFloat = 63 - 22
// Keep the source's rim-to-art proportions: rim width W with art side (824 - 2W) at the
// same scale as the rim.
let rim = (srcRim * body.width) / (srcSize - 2 * srcInset + 2 * srcRim)
let scale = (body.width - 2 * rim) / (srcSize - 2 * srcInset)
func inset(_ d: CGFloat) -> CGRect { body.insetBy(dx: d, dy: d) }
func srcPx(_ v: CGFloat) -> CGFloat { (v - 22) * scale }   // source rim offset → final px

// ── Render the 1024 master ───────────────────────────────────────────────────────────────
let space = CGColorSpace(name: CGColorSpace.sRGB)!
let ctx = CGContext(data: nil, width: Int(canvas), height: Int(canvas), bitsPerComponent: 8,
                    bytesPerRow: 0, space: space,
                    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
ctx.interpolationQuality = .high

let outline = squircle(body)

// Apple's template shadow: straight down, soft, about 30% black.
ctx.saveGState()
ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: 22, color: color(0x000000, 0.32))
ctx.addPath(outline); ctx.setFillColor(color(0x010408)); ctx.fillPath()
ctx.restoreGState()

ctx.saveGState()
ctx.addPath(outline); ctx.clip()

// Rim bevel: dark green at the edge brightening inward, lit a little more from the top, as
// in the source (top highlight is brighter than the sides and bottom).
ctx.saveGState()
ctx.addPath(outline); ctx.addPath(squircle(inset(srcPx(48)))); ctx.clip(using: .evenOdd)
let bevel = CGGradient(colorsSpace: space,
                       colors: [color(0x1E7A3A), color(0x0F5122), color(0x0D4F1E)] as CFArray,
                       locations: [0, 0.5, 1])!
ctx.drawLinearGradient(bevel, start: CGPoint(x: 0, y: body.maxY), end: CGPoint(x: 0, y: body.minY),
                       options: [])
ctx.restoreGState()

// Neon outer edge line and the bright inner highlight of the bevel.
func stroke(_ d: CGFloat, _ width: CGFloat, _ c: CGColor, glow: CGFloat = 0) {
    ctx.saveGState()
    if glow > 0 { ctx.setShadow(offset: .zero, blur: glow, color: c) }
    ctx.addPath(squircle(inset(d))); ctx.setStrokeColor(c); ctx.setLineWidth(width); ctx.strokePath()
    ctx.restoreGState()
}
stroke(srcPx(24), 2.5, color(0x3FD45C, 0.9), glow: 6)
stroke(srcPx(46.5), 2.2, color(0x88E8A3), glow: 8)

// Black channel, then the thin gray inner line.
ctx.saveGState()
ctx.addPath(squircle(inset(srcPx(48)))); ctx.addPath(squircle(inset(rim))); ctx.clip(using: .evenOdd)
ctx.setFillColor(color(0x000304)); ctx.fill(body)
ctx.restoreGState()
stroke(srcPx(60), 1.6, color(0x44474D))

// The art itself: orc, octagon and circuit traces, unchanged.
ctx.saveGState()
ctx.addPath(squircle(inset(rim))); ctx.clip()
let crop = art.cropping(to: CGRect(x: srcInset, y: srcInset,
                                   width: srcSize - 2 * srcInset, height: srcSize - 2 * srcInset))!
ctx.draw(crop, in: inset(rim))
ctx.restoreGState()

ctx.restoreGState()
let master = ctx.makeImage()!

// ── Write the PNGs, the .iconset and the .icns ───────────────────────────────────────────
func writePNG(_ image: CGImage, _ url: URL) {
    let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil)!
    CGImageDestinationAddImage(dest, image, nil)
    guard CGImageDestinationFinalize(dest) else { fatalError("Can't write \(url.path)") }
}
func resized(_ px: Int) -> CGImage {
    let c = CGContext(data: nil, width: px, height: px, bitsPerComponent: 8, bytesPerRow: 0,
                      space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    c.interpolationQuality = .high
    c.draw(master, in: CGRect(x: 0, y: 0, width: px, height: px))
    return c.makeImage()!
}

let iconset = here.appendingPathComponent("TheOrc.iconset")
try? FileManager.default.removeItem(at: iconset)
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
for pt in [16, 32, 128, 256, 512] {
    writePNG(resized(pt), iconset.appendingPathComponent("icon_\(pt)x\(pt).png"))
    writePNG(resized(pt * 2), iconset.appendingPathComponent("icon_\(pt)x\(pt)@2x.png"))
}
let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = ["-c", "icns", iconset.path, "-o", here.appendingPathComponent("TheOrc.icns").path]
try iconutil.run(); iconutil.waitUntilExit()
guard iconutil.terminationStatus == 0 else { fatalError("iconutil failed") }
try FileManager.default.removeItem(at: iconset)   // the .icns holds every size
print("Wrote TheOrc.icns (rim \(String(format: "%.1f", rim)) px, art scale \(String(format: "%.3f", scale)))")
