// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using ToolcallerBench;

if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
{
    PrintUsage();
    return 0;
}

try
{
    var options = ParseArgs(args);

    if (options.Suite != "validate")
        throw new ArgumentException($"Unknown suite '{options.Suite}'. Only 'validate' is implemented today.");

    if (string.IsNullOrWhiteSpace(options.CapturesDir) || !Directory.Exists(options.CapturesDir))
    {
        Console.Error.WriteLine("validate requires --captures pointing at a directory of toolcaller capture JSON files.");
        return 64;
    }

    var toolsPath = options.ToolsPath ?? Path.Combine(AppContext.BaseDirectory, "Schemas", "toolcaller_v0_frozen_tools.json");
    if (!File.Exists(toolsPath))
    {
        Console.Error.WriteLine($"Frozen tool inventory not found: {toolsPath}");
        return 64;
    }

    var toolsBytes = await File.ReadAllBytesAsync(toolsPath);
    // The canonical frozen-inventory hash is the LF (git blob) form. core.autocrlf
    // checkouts materialize CRLF on disk — normalize before hashing so a Windows
    // checkout doesn't reject every capture as stale-hash.
    var toolsHash = Convert.ToHexString(SHA256.HashData(NormalizeLineEndings(toolsBytes))).ToLowerInvariant();

    var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    var frozenTools = JsonSerializer.Deserialize<List<FrozenTool>>(toolsBytes, jsonOptions)
        ?? throw new InvalidOperationException("Frozen tool inventory parsed to null.");

    Console.WriteLine($"Frozen tool inventory: {frozenTools.Count} tools, sha256 {toolsHash}");

    var captureFiles = Directory.GetFiles(options.CapturesDir, "*.json", SearchOption.TopDirectoryOnly);
    if (captureFiles.Length == 0)
    {
        Console.Error.WriteLine($"No .json capture files found under: {options.CapturesDir}");
        return 64;
    }

    var captures = new List<ToolcallerCapture>();
    foreach (var file in captureFiles)
    {
        try
        {
            var capture = JsonSerializer.Deserialize<ToolcallerCapture>(await File.ReadAllBytesAsync(file), jsonOptions)
                ?? throw new InvalidOperationException("parsed to null");
            captures.Add(capture);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to parse {Path.GetFileName(file)}: {ex.Message}");
            return 65;
        }
    }

    Console.WriteLine($"Loaded {captures.Count} capture(s) from {options.CapturesDir}");

    var report = ToolcallerCaptureValidator.Validate(captures, frozenTools, toolsHash);
    var output = options.OutputDir ?? Path.Combine(Environment.CurrentDirectory, ".orc", "toolcaller-bench");
    var (jsonPath, markdownPath) = await ToolcallerReportWriter.WriteAsync(report, output);

    Console.WriteLine($"Verdict: {(report.Passed ? "PASS" : "FAIL")}, {report.PassedExamples}/{report.TotalExamples} examples passed");
    Console.WriteLine($"JSON: {jsonPath}");
    Console.WriteLine($"Markdown: {markdownPath}");

    if (!report.Passed)
    {
        foreach (var finding in report.Findings.Where(f => f.Severity == FindingSeverity.Error))
            Console.Error.WriteLine($"  [{finding.Gate}] {finding.ExampleId}: {finding.Detail}");
    }

    return report.Passed ? 0 : 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[ERROR] {ex.Message}");
    return 1;
}

static byte[] NormalizeLineEndings(byte[] bytes)
{
    var result = new List<byte>(bytes.Length);
    for (var i = 0; i < bytes.Length; i++)
    {
        if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
            continue;
        result.Add(bytes[i]);
    }
    return result.ToArray();
}

static void PrintUsage()
{
    Console.WriteLine("Usage: toolcaller-bench --suite validate --captures <folder> [options]");
    Console.WriteLine("  --suite <name>       Only 'validate' is implemented today.");
    Console.WriteLine("  --captures <folder>  Directory of toolcaller capture JSON files to validate.");
    Console.WriteLine("  --tools <path>       Override path to the frozen tool inventory JSON.");
    Console.WriteLine("                       Default: Schemas/toolcaller_v0_frozen_tools.json next to the exe.");
    Console.WriteLine("  --output <folder>    Report directory (default .orc/toolcaller-bench).");
    Console.WriteLine();
    Console.WriteLine("This tool implements mechanical dataset admission-gate validation only");
    Console.WriteLine("(training_pit/TOOLCALLER_CAPTURE_SCHEMA.md). It does not generate examples,");
    Console.WriteLine("run baselines, or call any model. See docs/THEORC_TOOLCALLER_V0.md for the");
    Console.WriteLine("full F-1 deliverable list this tool partially satisfies.");
}

static CliOptions ParseArgs(string[] args)
{
    string suite = "validate";
    string? capturesDir = null;
    string? toolsPath = null;
    string? outputDir = null;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--suite": suite = Next(args, ref i); break;
            case "--captures": capturesDir = Next(args, ref i); break;
            case "--tools": toolsPath = Next(args, ref i); break;
            case "--output": outputDir = Next(args, ref i); break;
            default: throw new ArgumentException($"Unknown option '{args[i]}'.");
        }
    }

    return new CliOptions(suite, capturesDir, toolsPath, outputDir);
}

static string Next(string[] args, ref int i)
{
    if (i + 1 >= args.Length)
        throw new ArgumentException($"Option '{args[i]}' requires a value.");
    return args[++i];
}

internal sealed record CliOptions(string Suite, string? CapturesDir, string? ToolsPath, string? OutputDir);
