using OrchestratorIDE.Core;

namespace OrchestratorIDE.Tools;

/// <summary>
/// Two-stage verification:
///   Stage 1 — Build check   (always runs on any project — catches compile errors)
///   Stage 2 — Test run      (only fires if test files actually exist)
///
/// This makes Auto-Verify useful from day one, not just on mature projects.
/// </summary>
public static class TestTools
{
    public static void Register(ToolRegistry registry, string workspaceRoot)
    {
        registry.Register(new ToolDefinition
        {
            Name        = "run_tests",
            Description = "Two-stage verify: (1) build/compile check always runs, " +
                          "(2) test suite runs only if test files are found. " +
                          "Auto-detects: dotnet, pytest, npm/tsc, cargo, go.",
            Parameters  = new()
            {
                ["path"] = new("string", "Path to verify. Defaults to workspace root."),
            },
            Required         = [],
            RequiresApproval = false,
            Handler = async (args, ct) =>
            {
                var root = args.TryGetValue("path", out var p) ? p?.ToString() ?? workspaceRoot : workspaceRoot;
                if (!Path.IsPathRooted(root)) root = Path.Combine(workspaceRoot, root);

                var lang = DetectLanguage(root);
                if (lang == Language.Unknown)
                    return "[verify] Could not detect project type — skipping verification.";

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[verify: {lang}]");

                // ── Stage 1: Build / compile ──────────────────────────────────
                sb.AppendLine("── Stage 1: Build check ──");
                var buildCmd = GetBuildCommand(lang, root);
                if (buildCmd != null)
                {
                    var buildOut = await ShellTools.RunAsync(buildCmd, root, ct, maxBytes: 8_000);
                    sb.AppendLine(buildOut);

                    // If build failed hard, skip tests — no point running them
                    if (BuildFailed(lang, buildOut))
                    {
                        sb.AppendLine("── Stage 2: Tests skipped (build failed) ──");
                        return sb.ToString();
                    }
                }
                else
                {
                    sb.AppendLine("(no build step for this project type)");
                }

                // ── Stage 2: Tests (only if test files exist) ─────────────────
                sb.AppendLine("── Stage 2: Test run ──");
                var testInfo = FindTests(lang, root);
                if (testInfo.Count == 0)
                {
                    sb.AppendLine($"No test files found — skipping. " +
                                  $"(Add tests to unlock this stage: {TestFileHint(lang)})");
                    return sb.ToString();
                }

                sb.AppendLine($"Found {testInfo.Count} test file(s). Running…");
                var testCmd = GetTestCommand(lang);
                var testOut = await ShellTools.RunAsync(testCmd, root, ct, maxBytes: 16_000);
                sb.AppendLine(testOut);

                return sb.ToString();
            }
        });
    }

    // ── Language detection ────────────────────────────────────────────────────

    private enum Language { Unknown, DotNet, Python, Node, Rust, Go }

    private static Language DetectLanguage(string root)
    {
        if (Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories).Length > 0)
            return Language.DotNet;
        if (File.Exists(Path.Combine(root, "Cargo.toml")))
            return Language.Rust;
        if (File.Exists(Path.Combine(root, "package.json")))
            return Language.Node;
        if (File.Exists(Path.Combine(root, "pyproject.toml"))
            || File.Exists(Path.Combine(root, "pytest.ini"))
            || File.Exists(Path.Combine(root, "setup.py"))
            || File.Exists(Path.Combine(root, "requirements.txt"))
            || Directory.GetFiles(root, "*.py", SearchOption.TopDirectoryOnly).Length > 0)
            return Language.Python;
        if (Directory.GetFiles(root, "*.go", SearchOption.AllDirectories).Length > 0)
            return Language.Go;
        return Language.Unknown;
    }

    // ── Build commands ────────────────────────────────────────────────────────

    private static string? GetBuildCommand(Language lang, string root) => lang switch
    {
        Language.DotNet  => "dotnet build --no-restore -v minimal",
        Language.Rust    => "cargo check 2>&1",
        Language.Go      => "go build ./...",
        Language.Node    => HasScript(root, "build")  ? "npm run build --if-present"
                          : HasTypeScript(root)        ? "npx tsc --noEmit"
                          : null,
        Language.Python  => BuildPyCompileCommand(root),
        _                => null,
    };

    private static bool BuildFailed(Language lang, string output) => lang switch
    {
        Language.DotNet => output.Contains("Build FAILED") || output.Contains("Error(s)"),
        Language.Rust   => output.Contains("error[E") || output.Contains("aborting due to"),
        Language.Go     => output.Contains(": syntax error") || output.Contains("undefined:"),
        Language.Node   => output.Contains("error TS") || output.Contains("npm ERR!"),
        Language.Python => output.Contains("SyntaxError") || output.Contains("IndentationError"),
        _               => false,
    };

    // ── Test file discovery ───────────────────────────────────────────────────

    private static List<string> FindTests(Language lang, string root)
    {
        try
        {
            return lang switch
            {
                Language.DotNet  => FindDotNetTests(root),
                Language.Python  => Directory.GetFiles(root, "test_*.py",  SearchOption.AllDirectories)
                                        .Concat(Directory.GetFiles(root, "*_test.py", SearchOption.AllDirectories))
                                        .ToList(),
                Language.Node    => Directory.GetFiles(root, "*.test.ts",  SearchOption.AllDirectories)
                                        .Concat(Directory.GetFiles(root, "*.test.js",  SearchOption.AllDirectories))
                                        .Concat(Directory.GetFiles(root, "*.spec.ts",  SearchOption.AllDirectories))
                                        .Concat(Directory.GetFiles(root, "*.spec.js",  SearchOption.AllDirectories))
                                        .Where(f => !f.Contains("node_modules"))
                                        .ToList(),
                Language.Rust    => FindRustTests(root),
                Language.Go      => Directory.GetFiles(root, "*_test.go", SearchOption.AllDirectories).ToList(),
                _                => [],
            };
        }
        catch { return []; }
    }

    private static List<string> FindDotNetTests(string root)
    {
        // Look for test project files (project name contains Test/Tests/Spec)
        var testProjects = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(f => Path.GetFileNameWithoutExtension(f)
                .Contains("test", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (testProjects.Count > 0) return testProjects;

        // Fall back: look for test files (xUnit, NUnit, MSTest attributes)
        return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => {
                var text = File.ReadAllText(f);
                return text.Contains("[Fact]") || text.Contains("[Test]")
                    || text.Contains("[TestMethod]") || text.Contains("[Theory]");
            })
            .ToList();
    }

    private static List<string> FindRustTests(string root)
    {
        return Directory.GetFiles(root, "*.rs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}target{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("#[test]"))
            .ToList();
    }

    // ── Test run commands ─────────────────────────────────────────────────────

    private static string GetTestCommand(Language lang) => lang switch
    {
        Language.DotNet => "dotnet test --no-build --logger \"console;verbosity=normal\"",
        Language.Python => "python -m pytest -v --tb=short",
        Language.Node   => "npm test --if-present",
        Language.Rust   => "cargo test",
        Language.Go     => "go test ./...",
        _               => "echo no test command"
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string TestFileHint(Language lang) => lang switch
    {
        Language.DotNet => "*Tests.csproj or [Fact]/[Test] attributes",
        Language.Python => "test_*.py or *_test.py files",
        Language.Node   => "*.test.ts / *.spec.ts files",
        Language.Rust   => "#[test] functions in any .rs file",
        Language.Go     => "*_test.go files",
        _               => "test files"
    };

    private static bool HasScript(string root, string scriptName)
    {
        try
        {
            var pkg = File.ReadAllText(Path.Combine(root, "package.json"));
            return pkg.Contains($"\"{scriptName}\"");
        }
        catch { return false; }
    }

    private static bool HasTypeScript(string root)
        => File.Exists(Path.Combine(root, "tsconfig.json"))
        || Directory.GetFiles(root, "*.ts", SearchOption.TopDirectoryOnly).Length > 0;

    /// <summary>
    /// Python has no single build step — compile-check each changed .py file.
    /// Falls back to checking all .py files in the top two levels.
    /// </summary>
    private static string? BuildPyCompileCommand(string root)
    {
        var pyFiles = Directory.GetFiles(root, "*.py", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetDirectories(root)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .SelectMany(d => Directory.GetFiles(d, "*.py", SearchOption.TopDirectoryOnly)))
            .Where(f => !f.Contains("__pycache__"))
            .ToList();

        if (pyFiles.Count == 0) return null;

        // python -m py_compile file1.py file2.py ...
        var files = string.Join(" ", pyFiles.Take(30).Select(f => $"\"{f}\""));
        return $"python -m py_compile {files}";
    }
}
