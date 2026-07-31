// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrchestratorIDE.Core.Runtime;

public enum RuntimeAssetKind
{
    BaseModelGguf,
    LoraGguf,
    PeftAdapterDirectory,
    Unknown,
}

public enum RuntimeRole
{
    Boss,
    Worker,
    Researcher,
    Reviewer,
}

public sealed record RuntimeModelAsset(
    string Id,
    RuntimeAssetKind Kind,
    string Path,
    string DisplayName,
    long? SizeBytes,
    DateTimeOffset LastModifiedUtc,
    IReadOnlyList<RuntimeRole> SuggestedRoles,
    string? BaseModelHint = null);

public sealed record RuntimeRoleBinding(
    RuntimeRole Role,
    RuntimeModelAsset BaseModel,
    RuntimeModelAsset? Adapter);

/// <summary>
/// Local Native Runtime model registry. Phase 3 starts here: discover already-present
/// GGUF models and PEFT/LoRA adapter assets, then resolve role-friendly bindings.
///
/// TODO: future ModelDepot.Fetch(...) may integrate Hugging Face Hub or installer
/// workflows. This first slice is intentionally local-only and performs no network I/O.
/// </summary>
public sealed class ModelDepot
{
    private static readonly Regex _tokenSplitter = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex _opaqueFileName = new(@"^[0-9a-f]{32,}\.gguf$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly StringComparer _pathComparer = StringComparer.OrdinalIgnoreCase;

    private static readonly IReadOnlyDictionary<RuntimeRole, string[]> _roleTokens =
        new Dictionary<RuntimeRole, string[]>
        {
            [RuntimeRole.Boss] = ["boss", "pitboss", "planner", "planning", "orchestrator"],
            [RuntimeRole.Worker] = ["worker", "coder", "coding", "code", "implementer"],
            [RuntimeRole.Researcher] = ["researcher", "research", "scout", "analysis"],
            [RuntimeRole.Reviewer] = ["reviewer", "review", "critic", "gate"],
        };

    private static readonly string[] _ignoredDirectoryNames =
    [
        ".git",
        ".hg",
        ".svn",
        ".cache",
        "bin",
        "obj",
        "node_modules",
        "packages",
    ];

    private ModelDepot(string root, IReadOnlyList<RuntimeModelAsset> assets)
    {
        Root = root;
        Assets = assets;
    }

    public string Root { get; }

    public IReadOnlyList<RuntimeModelAsset> Assets { get; }

    /// <summary>
    /// Returns a depot whose BASE MODEL candidates are restricted to those whose display name
    /// contains <paramref name="nameSubstring"/> (case-insensitive). Adapters are kept as-is.
    /// Used to pin role resolution to a specific model (e.g. a benchmark run that must not be
    /// affected by other models being added to or removed from a shared depot). Returns an
    /// empty-candidate depot if nothing matches — callers surface that as "not resolved".
    /// </summary>
    public ModelDepot WithBaseModelFilter(string nameSubstring)
    {
        if (string.IsNullOrWhiteSpace(nameSubstring))
            return this;
        return new ModelDepot(Root, Assets
            .Where(a => a.Kind != RuntimeAssetKind.BaseModelGguf ||
                        a.DisplayName.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase))
            .ToArray());
    }

    public static ModelDepot Scan(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return new ModelDepot("", []);

        string fullRoot;
        try
        {
            fullRoot = System.IO.Path.GetFullPath(root);
        }
        catch
        {
            return new ModelDepot(root, []);
        }

        if (!Directory.Exists(fullRoot))
            return new ModelDepot(fullRoot, []);

        var assets = new List<RuntimeModelAsset>();

        foreach (var file in EnumerateFiles(fullRoot))
        {
            if (!file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                continue;

            var kind = IsLoraGguf(file)
                ? RuntimeAssetKind.LoraGguf
                : RuntimeAssetKind.BaseModelGguf;

            var asset = CreateAsset(file, kind);
            if (asset is not null)
                assets.Add(asset);
        }

        foreach (var directory in EnumerateDirectories(fullRoot))
        {
            var configPath = System.IO.Path.Combine(directory, "adapter_config.json");
            if (!File.Exists(configPath))
                continue;

            var hasWeights =
                File.Exists(System.IO.Path.Combine(directory, "adapter_model.safetensors")) ||
                File.Exists(System.IO.Path.Combine(directory, "adapter_model.bin"));

            var asset = CreateAsset(
                directory,
                hasWeights ? RuntimeAssetKind.PeftAdapterDirectory : RuntimeAssetKind.Unknown,
                isDirectory: true,
                baseModelHint: TryReadBaseModelHint(configPath));
            if (asset is not null)
                assets.Add(asset);
        }

        return new ModelDepot(
            fullRoot,
            assets
                .OrderBy(a => a.Kind)
                .ThenBy(a => a.Path, _pathComparer)
                .ToArray());
    }

    /// <summary>
    /// Scans multiple folder roots and merges them into one depot, optionally also including
    /// every model Ollama has already pulled -- resolved directly from Ollama's own manifest/blob
    /// store (<see cref="ScanOllamaModels"/>), not downloaded again. Duplicate (path, display
    /// name) pairs are kept once, first source wins -- keyed on the PAIR, not path alone
    /// (grok-review MINOR, 2026-07-30): Ollama routinely aliases multiple tags to the same blob
    /// (e.g. "model:latest" and "model:14b" pointing at one file) -- deduping by path alone would
    /// silently drop one of those tag names from the depot, so a caller filtering by the dropped
    /// alias would never find a match even though the underlying file is right there.
    /// </summary>
    public static ModelDepot ScanSources(
        IEnumerable<string> roots,
        bool includeOllamaModels = false,
        string? ollamaModelsRootOverride = null)
    {
        var assets = new List<RuntimeModelAsset>();
        var seenKeys = new HashSet<(string Path, string DisplayName)>();

        void AddRange(IEnumerable<RuntimeModelAsset> source)
        {
            foreach (var asset in source)
                if (seenKeys.Add((asset.Path.ToLowerInvariant(), asset.DisplayName)))
                    assets.Add(asset);
        }

        var distinctRoots = roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(_pathComparer)
            .ToArray();

        foreach (var root in distinctRoots)
            AddRange(Scan(root).Assets);

        if (includeOllamaModels)
            AddRange(ScanOllamaModels(ollamaModelsRootOverride));

        return new ModelDepot(
            distinctRoots.FirstOrDefault() ?? "",
            assets.OrderBy(a => a.Kind).ThenBy(a => a.Path, _pathComparer).ToArray());
    }

    /// <summary>
    /// Resolves every model Ollama has pulled to its real, already-on-disk GGUF file --
    /// confirmed live 2026-07-30 by hex-dumping a blob: Ollama's model layer is a byte-for-byte
    /// standard GGUF file (magic bytes "GGUF"), just stored content-hashed with no extension
    /// (e.g. `blobs/sha256-ac9bc7a6...`) instead of a human-readable filename, tracked by a
    /// separate manifest under `manifests/&lt;host&gt;/&lt;namespace&gt;/&lt;name&gt;/&lt;tag&gt;`
    /// (an OCI/Docker-style image manifest JSON). No network I/O, no re-download -- this only
    /// reads files Ollama already wrote.
    /// </summary>
    /// <param name="rootOverride">
    /// Test/explicit-config seam. When null, resolves the same way Ollama's own server does:
    /// the OLLAMA_MODELS environment variable if set, else `%USERPROFILE%\.ollama\models`.
    /// </param>
    public static IEnumerable<RuntimeModelAsset> ScanOllamaModels(string? rootOverride = null)
    {
        var root = ResolveOllamaModelsRoot(rootOverride);
        var manifestsRoot = System.IO.Path.Combine(root, "manifests");
        var blobsRoot = System.IO.Path.Combine(root, "blobs");
        if (!Directory.Exists(manifestsRoot) || !Directory.Exists(blobsRoot))
            yield break;

        foreach (var manifestFile in EnumerateFiles(manifestsRoot))
        {
            var tagName = TagNameFromManifestPath(manifestsRoot, manifestFile);
            var asset = TryParseOllamaManifest(manifestFile, blobsRoot, tagName);
            if (asset is not null)
                yield return asset;
        }
    }

    private static string ResolveOllamaModelsRoot(string? rootOverride)
    {
        if (!string.IsNullOrWhiteSpace(rootOverride))
            return rootOverride;

        var envRoot = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        if (!string.IsNullOrWhiteSpace(envRoot))
            return envRoot;

        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");
    }

    /// <summary>
    /// Ollama's manifest tree is `manifests/&lt;host&gt;/&lt;namespace&gt;/&lt;name&gt;/&lt;tag&gt;`
    /// -- Ollama's own CLI/API shorten the default `registry.ollama.ai/library/` host+namespace
    /// out of the displayed tag (e.g. "qwen2.5-coder:14b", not
    /// "registry.ollama.ai/library/qwen2.5-coder:14b"), matching what OrcChat's model dropdown
    /// (sourced from Ollama's own API) actually shows -- so the reconstructed name here must
    /// match that shortening exactly, or ModelDepot.WithBaseModelFilter's substring match against
    /// a caller's requested model name would never hit.
    /// </summary>
    private static string TagNameFromManifestPath(string manifestsRoot, string manifestFile)
    {
        var relative = System.IO.Path.GetRelativePath(manifestsRoot, manifestFile).Replace('\\', '/');

        // grok-review MINOR: the two strips are independent, not one fixed prefix -- Ollama's
        // own short-name convention drops the DEFAULT REGISTRY host whenever present (even for a
        // user's own non-"library" namespace, e.g. "registry.ollama.ai/someuser/model" ->
        // "someuser/model"), and separately collapses the "library" NAMESPACE specifically only
        // when it's the segment right after that host. Treating "registry.ollama.ai/library/" as
        // one fixed prefix silently kept the host for every non-library-namespace default-registry
        // model, which would never match the short name Ollama's own API (and OrcChat's dropdown)
        // actually shows.
        const string defaultRegistryHost = "registry.ollama.ai/";
        const string defaultNamespace = "library/";
        if (relative.StartsWith(defaultRegistryHost, StringComparison.OrdinalIgnoreCase))
        {
            relative = relative[defaultRegistryHost.Length..];
            if (relative.StartsWith(defaultNamespace, StringComparison.OrdinalIgnoreCase))
                relative = relative[defaultNamespace.Length..];
        }

        var lastSlash = relative.LastIndexOf('/');
        return lastSlash < 0 ? relative : $"{relative[..lastSlash]}:{relative[(lastSlash + 1)..]}";
    }

    private static RuntimeModelAsset? TryParseOllamaManifest(string manifestFile, string blobsRoot, string tagName)
    {
        try
        {
            using var stream = File.OpenRead(manifestFile);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var layer in layers.EnumerateArray())
            {
                if (!layer.TryGetProperty("mediaType", out var mediaType) ||
                    mediaType.GetString() != "application/vnd.ollama.image.model")
                    continue;

                if (!layer.TryGetProperty("digest", out var digestProp))
                    return null;
                var digest = digestProp.GetString();
                if (string.IsNullOrWhiteSpace(digest))
                    return null;

                // sha256:<hex> -> sha256-<hex>, Ollama's own blob filename convention.
                var blobPath = System.IO.Path.Combine(blobsRoot, digest.Replace(':', '-'));
                if (!File.Exists(blobPath))
                    return null;

                var sizeBytes = layer.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out var s)
                    ? s
                    : new FileInfo(blobPath).Length;

                return new RuntimeModelAsset(
                    // grok-review MINOR: hash (path, tag), not path alone -- Ollama routinely
                    // aliases multiple tags to one blob (e.g. "model:latest" == "model:14b"), and
                    // CreateId(blobPath) alone would give those distinct assets the same Id. Not
                    // reachable as a bug today (nothing keys a lookup by Id -- verified, zero
                    // consumers in the whole codebase), but it's a free fix, and ScanSources'
                    // own dedup already treats (path, displayName) as the real identity, so the
                    // Id should match that same identity instead of silently disagreeing with it.
                    Id: CreateId($"{blobPath}|{tagName}"),
                    Kind: RuntimeAssetKind.BaseModelGguf,
                    Path: blobPath,
                    DisplayName: tagName,
                    SizeBytes: sizeBytes,
                    LastModifiedUtc: new DateTimeOffset(File.GetLastWriteTimeUtc(blobPath), TimeSpan.Zero),
                    SuggestedRoles: InferRoles(tagName));
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public RuntimeRoleBinding? ResolveRole(RuntimeRole role)
        => ResolveRoleCore(role, workload: null);

    public RuntimeRoleBinding? ResolveRole(RuntimeRole role, RuntimeWorkloadKind workload)
        => ResolveRoleCore(role, workload);

    private RuntimeRoleBinding? ResolveRoleCore(RuntimeRole role, RuntimeWorkloadKind? workload)
    {
        var candidates = Assets.Where(a => a.Kind == RuntimeAssetKind.BaseModelGguf);
        if (workload is { } workloadKind)
        {
            // IsReasoningTuned is checked right after Verdict, ahead of the role-tag tie-break:
            // a reasoning-tuned model that happens to be tagged for this role must not beat a
            // non-reasoning model tagged for a different role when both tie on Verdict -- visible
            // <think> traces can consume the whole response budget before the required output
            // (see EvaluateContextFabric's IsReasoningTuned branch and HiveDispatchOptions).
            candidates = candidates
                .OrderByDescending(a => ModelAdmissionGate.Evaluate(a, workloadKind).Verdict)
                .ThenBy(a => ModelAdmissionGate.Fingerprint(a).IsReasoningTuned)
                .ThenByDescending(a => a.SuggestedRoles.Contains(role))
                .ThenByDescending(a => ModelAdmissionGate.Fingerprint(a).ParametersB ?? 0)
                .ThenBy(a => LooksOpaqueName(a.DisplayName))
                .ThenBy(a => a.Path, _pathComparer);
        }

        var baseModel = (workload is null
            ? candidates
            .OrderByDescending(a => a.SuggestedRoles.Contains(role))
            .ThenBy(a => LooksOpaqueName(a.DisplayName))
            .ThenBy(a => a.Path, _pathComparer)
            : candidates)
            .FirstOrDefault();

        if (baseModel is null)
            return null;

        var adapter = Assets
            .Where(a =>
                a.Kind is RuntimeAssetKind.LoraGguf or RuntimeAssetKind.PeftAdapterDirectory &&
                a.SuggestedRoles.Contains(role) &&
                IsCompatibleWithBase(a, baseModel))
            .OrderByDescending(a => AdapterAffinityScore(a, baseModel))
            .ThenBy(a => LooksOpaqueName(a.DisplayName))
            .ThenBy(a => a.Path, _pathComparer)
            .FirstOrDefault();

        return new RuntimeRoleBinding(role, baseModel, adapter);
    }

    private static RuntimeModelAsset? CreateAsset(
        string path,
        RuntimeAssetKind kind,
        bool isDirectory = false,
        string? baseModelHint = null)
    {
        try
        {
            var fullPath = System.IO.Path.GetFullPath(path);
            var lastModified = isDirectory
                ? Directory.GetLastWriteTimeUtc(fullPath)
                : File.GetLastWriteTimeUtc(fullPath);

            return new RuntimeModelAsset(
                Id: CreateId(fullPath),
                Kind: kind,
                Path: fullPath,
                DisplayName: isDirectory
                    ? new DirectoryInfo(fullPath).Name
                    : System.IO.Path.GetFileName(fullPath),
                SizeBytes: isDirectory ? null : new FileInfo(fullPath).Length,
                LastModifiedUtc: new DateTimeOffset(lastModified, TimeSpan.Zero),
                SuggestedRoles: InferRoles(fullPath),
                BaseModelHint: baseModelHint);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<RuntimeRole> InferRoles(string path)
    {
        var tokens = Tokenize(path);
        return _roleTokens
            .Where(pair => pair.Value.Any(tokens.Contains))
            .Select(pair => pair.Key)
            .ToArray();
    }

    private static bool IsLoraGguf(string path)
    {
        var tokens = Tokenize(path);
        return tokens.Contains("lora") ||
               tokens.Contains("adapter") ||
               tokens.Contains("adapters");
    }

    private static bool LooksOpaqueName(string displayName) =>
        _opaqueFileName.IsMatch(displayName);

    private static bool IsCompatibleWithBase(RuntimeModelAsset adapter, RuntimeModelAsset baseModel)
    {
        if (adapter.Kind != RuntimeAssetKind.PeftAdapterDirectory ||
            string.IsNullOrWhiteSpace(adapter.BaseModelHint))
            return true;

        return ModelNamesLikelyMatch(adapter.BaseModelHint, baseModel.DisplayName);
    }

    private static int AdapterAffinityScore(RuntimeModelAsset adapter, RuntimeModelAsset baseModel)
    {
        if (adapter.Kind == RuntimeAssetKind.PeftAdapterDirectory &&
            !string.IsNullOrWhiteSpace(adapter.BaseModelHint) &&
            ModelNamesLikelyMatch(adapter.BaseModelHint, baseModel.DisplayName))
            return 2;

        return adapter.Kind == RuntimeAssetKind.LoraGguf ? 1 : 0;
    }

    private static bool ModelNamesLikelyMatch(string hint, string baseDisplayName)
    {
        var hintTokens = ModelNameTokens(hint);
        var baseTokens = ModelNameTokens(baseDisplayName);
        if (hintTokens.Count == 0 || baseTokens.Count == 0)
            return false;

        var overlap = hintTokens.Count(baseTokens.Contains);
        return overlap >= Math.Min(2, hintTokens.Count);
    }

    private static HashSet<string> ModelNameTokens(string value)
    {
        var name = value.Replace('\\', '/').Split('/').Last();
        name = System.IO.Path.GetFileNameWithoutExtension(name);
        return _tokenSplitter
            .Split(name.ToLowerInvariant())
            .Where(token =>
                token.Length >= 2 &&
                token is not "q4" and not "q5" and not "q6" and not "q8" and not "gguf" and not "instruct")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> Tokenize(string value)
    {
        var lower = value.ToLowerInvariant();
        return _tokenSplitter
            .Split(lower)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string CreateId(string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return Convert.ToHexString(bytes[..8]).ToLowerInvariant();
    }

    private static string? TryReadBaseModelHint(string configPath)
    {
        try
        {
            using var stream = File.OpenRead(configPath);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.TryGetProperty("base_model_name_or_path", out var property)
                ? property.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(SafeFullPath(current)))
                continue;

            foreach (var directory in SafeEnumerateDirectories(current))
            {
                if (ShouldSkipDirectory(directory))
                    continue;
                pending.Push(directory);
            }

            foreach (var file in SafeEnumerateFilePaths(current))
                yield return file;
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string root)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(SafeFullPath(current)))
                continue;

            foreach (var directory in SafeEnumerateDirectories(current))
            {
                if (ShouldSkipDirectory(directory))
                    continue;

                yield return directory;
                pending.Push(directory);
            }
        }
    }

    private static bool ShouldSkipDirectory(string directory)
    {
        var name = new DirectoryInfo(directory).Name;
        if (_ignoredDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return true;

        try
        {
            return File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return true;
        }
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory);
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateFilePaths(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory);
        }
        catch
        {
            return [];
        }
    }
}
