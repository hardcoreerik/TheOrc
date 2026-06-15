// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorSetup.Services;

/// <summary>
/// Snapshot of download state fired via <see cref="DownloadService.OnProgress"/>.
/// All values are safe to read from any thread; marshal to UI thread before binding.
/// </summary>
public sealed class DownloadProgress
{
    public string ItemName         { get; init; } = "";
    public long   BytesReceived    { get; init; }
    public long   TotalBytes       { get; init; }
    public double SpeedBytesPerSec { get; init; }
    public bool   IsComplete       { get; init; }
    public string? Error           { get; init; }

    // ── Computed display helpers ──────────────────────────────────────────────

    public double Percent =>
        TotalBytes > 0 ? Math.Min(100.0, (double)BytesReceived / TotalBytes * 100.0) : 0;

    public string ReceivedDisplay =>
        BytesReceived >= 1_073_741_824
            ? $"{BytesReceived / 1_073_741_824.0:F2} GB"
            : $"{BytesReceived / 1_048_576.0:F0} MB";

    public string TotalDisplay =>
        TotalBytes >= 1_073_741_824
            ? $"{TotalBytes / 1_073_741_824.0:F1} GB"
            : $"{TotalBytes / 1_048_576.0:F0} MB";

    public string SpeedDisplay =>
        SpeedBytesPerSec >= 1_048_576
            ? $"{SpeedBytesPerSec / 1_048_576.0:F1} MB/s"
            : SpeedBytesPerSec >= 1024
                ? $"{SpeedBytesPerSec / 1024.0:F0} KB/s"
                : $"{SpeedBytesPerSec:F0} B/s";

    public string EtaDisplay
    {
        get
        {
            if (IsComplete || SpeedBytesPerSec <= 0 || TotalBytes <= 0) return "";
            var remainingBytes = TotalBytes - BytesReceived;
            var seconds        = remainingBytes / SpeedBytesPerSec;
            var ts             = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}h {ts.Minutes:D2}m remaining"
                : ts.TotalMinutes >= 1
                    ? $"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s remaining"
                    : $"{(int)ts.TotalSeconds}s remaining";
        }
    }

    public string StatusLine =>
        Error is not null
            ? $"Error: {Error}"
            : IsComplete
                ? $"✓ Complete  ({TotalDisplay})"
                : TotalBytes > 0
                    ? $"{ReceivedDisplay} / {TotalDisplay}  •  {SpeedDisplay}  •  {EtaDisplay}"
                    : $"{ReceivedDisplay} downloaded  •  {SpeedDisplay}";
}
