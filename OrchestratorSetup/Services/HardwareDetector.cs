using System.Diagnostics;
using System.Management;
using Microsoft.Win32;

namespace OrchestratorSetup.Services;

/// <summary>
/// Probes GPU name, VRAM, CUDA version, and available drive space
/// using WMI, registry reads, and file-version checks.
/// All I/O is wrapped in try/catch — detection failures are non-fatal.
/// </summary>
public static class HardwareDetector
{
    // ── Public result record ──────────────────────────────────────────────────

    public sealed record HardwareInfo
    {
        public string GpuName          { get; init; } = "Unknown";
        public int    VramGb           { get; init; } = 0;

        /// <summary>"nvidia" | "amd" | "intel" | "none"</summary>
        public string Vendor           { get; init; } = "none";

        /// <summary>"12.x" | "11.x" | "" (empty = no CUDA / unknown)</summary>
        public string CudaVersion      { get; init; } = "";

        /// <summary>"cuda12" | "cuda11" | "vulkan" | "avx2" | "cpu"</summary>
        public string RuntimeVariant   { get; init; } = "cpu";

        public long   SystemRamGb      { get; init; } = 0;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs hardware detection on a thread pool thread so the UI stays
    /// responsive during slow WMI queries.
    /// </summary>
    public static Task<HardwareInfo> DetectAsync(
        IProgress<string>? log = null,
        CancellationToken  ct  = default)
        => Task.Run(() => Detect(log), ct);

    // ── Core detection (synchronous, runs on thread pool) ────────────────────

    private static HardwareInfo Detect(IProgress<string>? log)
    {
        string gpuName  = "Unknown";
        long   vramBytes = 0;
        string vendor   = "none";
        string cuda     = "";
        long   ramGb    = 0;

        // ── 1. WMI Win32_VideoController ─────────────────────────────────────
        try
        {
            log?.Report("Querying Win32_VideoController…");
            using var mos = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM FROM Win32_VideoController");

            foreach (ManagementObject obj in mos.Get())
            {
                string name = obj["Name"]?.ToString()?.Trim() ?? "";
                if (string.IsNullOrEmpty(name)) continue;

                // Skip Microsoft software renderers and VMware adapters
                if (IsSoftwareAdapter(name)) continue;

                // Prefer the first discrete GPU we find
                if (gpuName != "Unknown") continue;

                gpuName = name;

                // AdapterRAM is a uint32 — wraps at 4,294,967,295 (≈ 4 GB).
                // We treat it as a lower bound; the registry pass below may raise it.
                if (obj["AdapterRAM"] is uint ar)
                    vramBytes = ar;

                log?.Report($"  Found: {name}");
            }
        }
        catch (Exception ex)
        {
            log?.Report($"WMI VideoController query failed: {ex.Message}");
        }

        // ── 2. Derive vendor from GPU name ────────────────────────────────────
        vendor = InferVendor(gpuName);
        log?.Report($"Vendor: {vendor}");

        // ── 3. VRAM from registry (handles > 4 GB cards) ─────────────────────
        try
        {
            long regVram = QueryRegistryVram(log);
            if (regVram > vramBytes)
            {
                vramBytes = regVram;
                log?.Report($"Registry VRAM: {regVram / (1024L * 1024L * 1024L)} GB");
            }
        }
        catch (Exception ex)
        {
            log?.Report($"Registry VRAM query failed: {ex.Message}");
        }

        // ── 4. CUDA detection (NVIDIA only) ───────────────────────────────────
        if (vendor == "nvidia")
        {
            cuda = DetectCudaVersion(log);
            log?.Report($"CUDA: {(string.IsNullOrEmpty(cuda) ? "not detected" : cuda)}");
        }

        // ── 5. System RAM ─────────────────────────────────────────────────────
        try
        {
            using var cs = new ManagementObjectSearcher(
                "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (ManagementObject obj in cs.Get())
            {
                if (obj["TotalPhysicalMemory"] is ulong total)
                    ramGb = (long)(total / (1024UL * 1024UL * 1024UL));
            }
        }
        catch { /* non-fatal */ }

        // ── 6. Pick runtime variant ───────────────────────────────────────────
        bool hasAvx2    = System.Runtime.Intrinsics.X86.Avx2.IsSupported;
        int  vramGb     = vramBytes > 0 ? (int)(vramBytes / (1024L * 1024L * 1024L)) : 0;
        string variant  = PickVariant(vendor, cuda, hasAvx2);

        log?.Report($"AVX2: {hasAvx2}  |  Variant selected: {variant}");
        log?.Report("Detection complete.");

        return new HardwareInfo
        {
            GpuName        = gpuName,
            VramGb         = vramGb,
            Vendor         = vendor,
            CudaVersion    = cuda,
            RuntimeVariant = variant,
            SystemRamGb    = ramGb,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsSoftwareAdapter(string name)
    {
        var n = name.AsSpan();
        return n.ContainsAny("Microsoft Basic".AsSpan())
            || n.Contains("VMware".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || n.Contains("VirtualBox".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || n.Contains("Remote Desktop".AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    private static string InferVendor(string name)
    {
        var n = name.ToUpperInvariant();
        if (n.Contains("NVIDIA") || n.Contains("GEFORCE") ||
            n.Contains("QUADRO") || n.Contains("TESLA"))
            return "nvidia";
        if (n.Contains("AMD") || n.Contains("RADEON") ||
            n.Contains("FIREPRO") || n.Contains("RX 6") || n.Contains("RX 7"))
            return "amd";
        if (n.Contains("INTEL") || n.Contains("ARC ") || n.Contains("IRIS"))
            return "intel";
        return "none";
    }

    /// <summary>
    /// Reads VRAM from the display-class registry key as a QWORD.
    /// This returns the correct value even on cards with > 4 GB VRAM.
    /// </summary>
    private static long QueryRegistryVram(IProgress<string>? log)
    {
        // GPU device keys live under the display-adapter class GUID
        const string classPath =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        using var classKey = Registry.LocalMachine.OpenSubKey(classPath, writable: false);
        if (classKey is null) return 0;

        long best = 0;

        foreach (string subName in classKey.GetSubKeyNames())
        {
            // Subkey names are zero-padded integers ("0000", "0001", …); skip "Properties"
            if (!int.TryParse(subName, out _)) continue;

            using var sub = classKey.OpenSubKey(subName, writable: false);
            if (sub is null) continue;

            // "HardwareInformation.qwMemorySize" — REG_BINARY, 8 bytes, LE int64
            object? raw = sub.GetValue("HardwareInformation.qwMemorySize");

            long mem = 0;

            if (raw is byte[] bytes)
            {
                if (bytes.Length == 8) mem = BitConverter.ToInt64(bytes, 0);
                else if (bytes.Length == 4) mem = BitConverter.ToUInt32(bytes, 0);
            }
            else if (raw is int dw && dw > 0)
            {
                mem = (uint)dw; // treat as unsigned to avoid sign-extension
            }
            else if (raw is long lw && lw > 0)
            {
                mem = lw;
            }

            if (mem > best) best = mem;
        }

        return best;
    }

    /// <summary>
    /// Detects the installed CUDA runtime version using three approaches in order:
    /// 1. nvcuda.dll product version string (most accurate, always present with driver)
    /// 2. CUDA Toolkit registry key (only present if Toolkit is installed)
    /// 3. nvml.dll existence as a ≥ 11.x fallback
    /// </summary>
    private static string DetectCudaVersion(IProgress<string>? log)
    {
        // 1. nvcuda.dll — always shipped with NVIDIA display driver
        try
        {
            string sys32   = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string nvcuda  = Path.Combine(sys32, "nvcuda.dll");

            if (File.Exists(nvcuda))
            {
                var fi = FileVersionInfo.GetVersionInfo(nvcuda);

                // ProductVersion is typically "12.4.131" or "11.8.89"
                // FileVersion  is typically "12.4.131.0" or similar
                string pv = fi.ProductVersion ?? fi.FileVersion ?? "";

                // Strip any trailing non-numeric suffix (e.g. " [DRIVER_BRANCH=r560]")
                int space = pv.IndexOf(' ');
                if (space > 0) pv = pv[..space];

                var parts = pv.Split('.');
                if (parts.Length >= 2
                    && int.TryParse(parts[0], out int major)
                    && int.TryParse(parts[1], out int minor)
                    && major >= 9 && major <= 20) // sanity-check realistic CUDA major
                {
                    log?.Report($"nvcuda.dll version: {major}.{minor}");
                    return $"{major}.{minor}";
                }
            }
        }
        catch (Exception ex)
        {
            log?.Report($"nvcuda.dll check failed: {ex.Message}");
        }

        // 2. CUDA Toolkit registry (optional install)
        try
        {
            const string cudaKey =
                @"SOFTWARE\NVIDIA Corporation\GPU Computing Toolkit\CUDA";

            using var key = Registry.LocalMachine.OpenSubKey(cudaKey, writable: false);
            if (key is not null)
            {
                // Subkeys look like "v12.4", "v11.8", …
                string[] versions = key.GetSubKeyNames()
                    .Where(n => n.StartsWith('v'))
                    .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (versions.Length > 0)
                {
                    string v = versions[0].TrimStart('v', 'V');
                    log?.Report($"CUDA toolkit registry: {v}");
                    return v;
                }
            }
        }
        catch { /* non-fatal */ }

        // 3. nvml.dll existence → assume at least 11.x
        try
        {
            string sys32  = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string nvml   = Path.Combine(sys32, "nvml.dll");

            // nvml.dll ships with every driver since CUDA 7; if we reach here
            // (nvcuda.dll didn't give a version) we conservatively say 11.x.
            if (File.Exists(nvml))
            {
                log?.Report("nvml.dll found — assuming CUDA ≥ 11.x");
                return "11.x";
            }
        }
        catch { /* non-fatal */ }

        return "";
    }

    /// <summary>
    /// Chooses the best llama.cpp runtime variant for the detected hardware.
    /// Order of preference: cuda12 > cuda11 > vulkan > avx2 > cpu.
    /// </summary>
    private static string PickVariant(string vendor, string cuda, bool hasAvx2)
    {
        if (vendor == "nvidia" && cuda.StartsWith("12")) return "cuda12";
        if (vendor == "nvidia" && cuda.StartsWith("11")) return "cuda11";
        if (vendor == "nvidia" && !string.IsNullOrEmpty(cuda)) return "cuda11"; // unknown minor → safer pick
        if (vendor is "amd" or "intel") return "vulkan";
        return hasAvx2 ? "avx2" : "cpu";
    }

    // ── Drive space helper (used by pages) ────────────────────────────────────

    /// <summary>
    /// Returns available free bytes on the drive that contains <paramref name="path"/>.
    /// Returns -1 if the path does not exist or the query fails.
    /// </summary>
    public static long GetFreeBytes(string path)
    {
        try
        {
            // Walk up to find an existing ancestor (the path itself may not exist yet)
            string? dir = path;
            while (dir is not null && !Directory.Exists(dir))
                dir = Path.GetDirectoryName(dir);

            if (dir is null) return -1;

            string root = Path.GetPathRoot(dir) ?? dir;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }
}
