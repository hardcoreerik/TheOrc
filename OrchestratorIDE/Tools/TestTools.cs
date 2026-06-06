using OrchestratorIDE.Core;

namespace OrchestratorIDE.Tools;

public static class TestTools
{
    public static void Register(ToolRegistry registry, string workspaceRoot)
    {
        registry.Register(new ToolDefinition
        {
            Name = "run_tests",
            Description = "Run the project's test suite. Auto-detects framework (dotnet, pytest, npm, cargo).",
            Parameters = new()
            {
                ["path"] = new("string", "Path to run tests from. Defaults to workspace root."),
            },
            Required = [],
            RequiresApproval = false,
            Handler = async (args, ct) =>
            {
                var path = args.TryGetValue("path", out var p) ? p?.ToString() ?? workspaceRoot : workspaceRoot;
                if (!Path.IsPathRooted(path)) path = Path.Combine(workspaceRoot, path);

                var (cmd, framework) = DetectFramework(path);
                if (cmd == null) return "[run_tests] Could not detect test framework. No tests found.";

                var result = await ShellTools.RunAsync(cmd, path, ct, maxBytes: 16_000);
                return $"[run_tests: {framework}]\n{result}";
            }
        });
    }

    private static (string? cmd, string framework) DetectFramework(string root)
    {
        // .NET / xUnit / NUnit / MSTest
        if (Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories).Length > 0)
            return ("dotnet test --logger \"console;verbosity=normal\"", "dotnet");

        // Python / pytest
        if (File.Exists(Path.Combine(root, "pytest.ini"))
            || File.Exists(Path.Combine(root, "pyproject.toml"))
            || Directory.Exists(Path.Combine(root, "tests")))
            return ("python -m pytest -v --tb=short", "pytest");

        // Node / npm
        if (File.Exists(Path.Combine(root, "package.json")))
            return ("npm test --if-present", "npm");

        // Rust / cargo
        if (File.Exists(Path.Combine(root, "Cargo.toml")))
            return ("cargo test", "cargo");

        // Go
        if (Directory.GetFiles(root, "*.go", SearchOption.AllDirectories).Length > 0)
            return ("go test ./...", "go");

        return (null, "");
    }
}
