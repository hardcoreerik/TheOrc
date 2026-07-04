// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OrchestratorIDE.Models;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.Services.Hive;

/// <summary>Paired-mobile command and terminal execution behind TheOrc approvals.</summary>
public sealed class HiveRemoteControlService : IDisposable
{
    private const int MaxOutputChars = 1_000_000;
    private static readonly TimeSpan TerminalIdleTimeout = TimeSpan.FromMinutes(30);
    private readonly ApprovalQueue _approvals;
    private readonly Dictionary<string, CommandRun> _commands = [];
    private readonly Dictionary<string, TerminalRun> _terminals = [];
    private readonly Lock _lock = new();
    private readonly Lock _auditLock = new();
    private readonly string _auditPath;

    public HiveRemoteControlService(ApprovalQueue approvals, string? auditPath = null)
    {
        _approvals = approvals;
        _auditPath = auditPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TheOrc", "hive-control-audit.jsonl");
    }

    public sealed record CommandRequest(string Command, string? Cwd = null, string? Reason = null);
    public sealed record TerminalRequest(string? Cwd = null, string? Reason = null);
    public sealed record TerminalInput(string Input);
    public sealed record ApprovalDecision(bool Approved);
    public sealed record CommandSnapshot(string Id, string Status, string Output, int? ExitCode, string? Error);
    public sealed record TerminalSnapshot(string Id, string Status, string Output, int Offset, int? ExitCode, string? Error);
    public sealed record ApprovalSnapshot(string Id, string Kind, string Summary, DateTime RequestedAt);

    public CommandSnapshot CreateCommand(string ownerNodeId, CommandRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Command)) throw new ArgumentException("Command is required.");
        var id = Guid.NewGuid().ToString("N");
        var call = NewApproval(id, "remote_shell", request.Command, request.Cwd, request.Reason);
        var run = new CommandRun(id, ownerNodeId, call);
        Audit("command_requested", ownerNodeId, id, new { request.Command, request.Cwd, request.Reason });
        lock (_lock) _commands[id] = run;
        _ = RunCommandAfterApprovalAsync(run, request);
        return Snapshot(run);
    }

    public CommandSnapshot? GetCommand(string ownerNodeId, string id)
    {
        lock (_lock) return _commands.TryGetValue(id, out var run) && run.OwnerNodeId == ownerNodeId
            ? Snapshot(run) : null;
    }

    public bool CancelCommand(string ownerNodeId, string id)
    {
        lock (_lock)
        {
            if (!_commands.TryGetValue(id, out var run) || run.OwnerNodeId != ownerNodeId) return false;
            run.Cancellation.Cancel();
            Audit("command_cancelled", ownerNodeId, id, null);
            return true;
        }
    }

    public TerminalSnapshot CreateTerminal(string ownerNodeId, TerminalRequest request)
    {
        PruneTerminals();
        var id = Guid.NewGuid().ToString("N");
        var call = NewApproval(id, "remote_terminal", "Open interactive PowerShell terminal", request.Cwd, request.Reason);
        var run = new TerminalRun(id, ownerNodeId, call);
        Audit("terminal_requested", ownerNodeId, id, new { request.Cwd, request.Reason });
        lock (_lock) _terminals[id] = run;
        _ = StartTerminalAfterApprovalAsync(run, request);
        return Snapshot(run, 0);
    }

    public TerminalSnapshot? GetTerminal(string ownerNodeId, string id, int offset)
    {
        PruneTerminals();
        lock (_lock)
        {
            if (!_terminals.TryGetValue(id, out var run) || run.OwnerNodeId != ownerNodeId) return null;
            run.LastActivity = DateTime.UtcNow;
            return Snapshot(run, Math.Max(0, offset));
        }
    }

    public bool WriteTerminal(string ownerNodeId, string id, TerminalInput input)
    {
        if (input.Input.Length > 32_768) throw new ArgumentException("Terminal input is too large.");
        lock (_lock)
        {
            if (!_terminals.TryGetValue(id, out var run) || run.OwnerNodeId != ownerNodeId
                || run.Process is not { HasExited: false }) return false;
            Audit("terminal_input", ownerNodeId, id, new { input.Input });
            run.Process.StandardInput.WriteLine(input.Input);
            run.Process.StandardInput.Flush();
            run.LastActivity = DateTime.UtcNow;
            return true;
        }
    }

    public bool CloseTerminal(string ownerNodeId, string id)
    {
        lock (_lock)
        {
            if (!_terminals.TryGetValue(id, out var run) || run.OwnerNodeId != ownerNodeId) return false;
            _terminals.Remove(id);
            run.Dispose();
            Audit("terminal_closed", ownerNodeId, id, null);
            return true;
        }
    }

    public ApprovalSnapshot[] GetApprovals(string ownerNodeId)
    {
        lock (_lock)
        {
            var owned = _commands.Values.Where(x => x.OwnerNodeId == ownerNodeId).Select(x => x.Approval)
                .Concat(_terminals.Values.Where(x => x.OwnerNodeId == ownerNodeId).Select(x => x.Approval))
                .ToDictionary(x => x.Id);
            return _approvals.Pending.Where(x => owned.ContainsKey(x.Call.Id)).Select(x =>
                new ApprovalSnapshot(x.Call.Id, x.Call.Name,
                    x.Call.Arguments.TryGetValue("command", out var command) ? command?.ToString() ?? x.Call.Name : x.Call.Name,
                    x.RequestedAt)).ToArray();
        }
    }

    public bool DecideApproval(string ownerNodeId, string id, ApprovalDecision decision)
    {
        lock (_lock)
        {
            var owns = _commands.Values.Any(x => x.OwnerNodeId == ownerNodeId && x.Approval.Id == id)
                || _terminals.Values.Any(x => x.OwnerNodeId == ownerNodeId && x.Approval.Id == id);
            if (!owns) return false;
            var pending = _approvals.Pending.FirstOrDefault(x => x.Call.Id == id);
            if (pending is null) return false;
            if (decision.Approved) _approvals.Approve(pending); else _approvals.Reject(pending);
            Audit(decision.Approved ? "approval_granted" : "approval_rejected", ownerNodeId, id, null);
            return true;
        }
    }

    public void RevokeOwner(string ownerNodeId)
    {
        lock (_lock)
        {
            foreach (var run in _commands.Values.Where(x => x.OwnerNodeId == ownerNodeId))
            {
                run.Cancellation.Cancel();
                Kill(run.Process);
            }
            foreach (var run in _terminals.Values.Where(x => x.OwnerNodeId == ownerNodeId).ToArray())
            {
                _terminals.Remove(run.Id);
                run.Dispose();
            }
            Audit("peer_control_revoked", ownerNodeId, "", null);
        }
    }

    private async Task RunCommandAfterApprovalAsync(CommandRun run, CommandRequest request)
    {
        try
        {
            run.Status = "awaiting_approval";
            if (!await _approvals.RequestApprovalAsync(run.Approval, run.Cancellation.Token).ConfigureAwait(false))
            {
                run.Status = "rejected";
                Audit("command_rejected", run.OwnerNodeId, run.Id, null);
                return;
            }
            run.Status = "running";
            var cwd = ResolveCwd(request.Cwd);
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(request.Command));
            using var process = StartPowerShell($"-NoProfile -NonInteractive -EncodedCommand {encoded}", cwd);
            run.Process = process;
            var stdout = process.StandardOutput.ReadToEndAsync(run.Cancellation.Token);
            var stderr = process.StandardError.ReadToEndAsync(run.Cancellation.Token);
            await process.WaitForExitAsync(run.Cancellation.Token).ConfigureAwait(false);
            run.ExitCode = process.ExitCode;
            run.Output = Limit((await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false)));
            run.Status = "complete";
            Audit("command_completed", run.OwnerNodeId, run.Id, new { run.ExitCode });
        }
        catch (OperationCanceledException) { run.Status = "cancelled"; Kill(run.Process); Audit("command_cancelled", run.OwnerNodeId, run.Id, null); }
        catch (Exception ex) { run.Status = "failed"; run.Error = ex.Message; Kill(run.Process); Audit("command_failed", run.OwnerNodeId, run.Id, new { error = ex.Message }); }
    }

    private async Task StartTerminalAfterApprovalAsync(TerminalRun run, TerminalRequest request)
    {
        try
        {
            run.Status = "awaiting_approval";
            if (!await _approvals.RequestApprovalAsync(run.Approval, run.Cancellation.Token).ConfigureAwait(false))
            {
                run.Status = "rejected";
                Audit("terminal_rejected", run.OwnerNodeId, run.Id, null);
                return;
            }
            var process = StartPowerShell("-NoLogo -NoProfile -Command -", ResolveCwd(request.Cwd));
            run.Process = process;
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) run.Append(e.Data + "\n"); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) run.Append("[stderr] " + e.Data + "\n"); };
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => { run.ExitCode = process.ExitCode; run.Status = "closed"; };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            run.Status = "open";
            Audit("terminal_opened", run.OwnerNodeId, run.Id, null);
        }
        catch (OperationCanceledException) { run.Status = "cancelled"; Audit("terminal_cancelled", run.OwnerNodeId, run.Id, null); }
        catch (Exception ex) { run.Status = "failed"; run.Error = ex.Message; Audit("terminal_failed", run.OwnerNodeId, run.Id, new { error = ex.Message }); }
    }

    private static ToolCall NewApproval(string id, string name, string command, string? cwd, string? reason) => new()
    {
        Id = id,
        Name = name,
        RequiresApproval = true,
        Status = ToolCallStatus.AwaitingApproval,
        ExplainWhy = reason,
        Arguments = new() { ["command"] = command, ["cwd"] = cwd ?? "" },
    };

    private static Process StartPowerShell(string arguments, string cwd) => Process.Start(new ProcessStartInfo
    {
        FileName = OperatingSystem.IsWindows() ? "powershell" : "pwsh",
        Arguments = arguments,
        WorkingDirectory = cwd,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    }) ?? throw new InvalidOperationException("Failed to start PowerShell.");

    private static string ResolveCwd(string? cwd)
    {
        var resolved = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : Path.GetFullPath(cwd);
        if (!Directory.Exists(resolved)) throw new DirectoryNotFoundException(resolved);
        return resolved;
    }

    private static string Limit(string value) => value.Length <= MaxOutputChars ? value : value[..MaxOutputChars] + "\n[truncated]";
    private static void Kill(Process? process) { try { if (process is { HasExited: false }) process.Kill(true); } catch { } }
    private static CommandSnapshot Snapshot(CommandRun run) => new(run.Id, run.Status, run.Output, run.ExitCode, run.Error);
    private static TerminalSnapshot Snapshot(TerminalRun run, int offset)
    {
        var output = run.ReadOutput();
        offset = Math.Min(offset, output.Length);
        return new(run.Id, run.Status, output[offset..], output.Length, run.ExitCode, run.Error);
    }

    private void PruneTerminals()
    {
        lock (_lock)
        {
            var expired = _terminals.Values.Where(x => DateTime.UtcNow - x.LastActivity > TerminalIdleTimeout).ToArray();
            foreach (var run in expired) { _terminals.Remove(run.Id); run.Dispose(); }
        }
    }

    private void Audit(string eventType, string nodeId, string id, object? detail)
    {
        var line = JsonSerializer.Serialize(new { timestamp = DateTime.UtcNow, eventType, nodeId, id, detail });
        lock (_auditLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_auditPath)!);
            File.AppendAllText(_auditPath, line + Environment.NewLine);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var run in _commands.Values) { run.Cancellation.Cancel(); Kill(run.Process); }
            foreach (var run in _terminals.Values) run.Dispose();
            _commands.Clear();
            _terminals.Clear();
        }
    }

    private sealed class CommandRun(string id, string ownerNodeId, ToolCall approval)
    {
        public string Id { get; } = id; public string OwnerNodeId { get; } = ownerNodeId; public ToolCall Approval { get; } = approval;
        public CancellationTokenSource Cancellation { get; } = new(); public Process? Process { get; set; }
        public string Status { get; set; } = "pending"; public string Output { get; set; } = "";
        public int? ExitCode { get; set; } public string? Error { get; set; }
    }

    private sealed class TerminalRun(string id, string ownerNodeId, ToolCall approval) : IDisposable
    {
        private readonly StringBuilder _output = new(); private readonly Lock _outputLock = new();
        public string Id { get; } = id; public string OwnerNodeId { get; } = ownerNodeId; public ToolCall Approval { get; } = approval;
        public CancellationTokenSource Cancellation { get; } = new(); public Process? Process { get; set; }
        public string Status { get; set; } = "pending"; public int? ExitCode { get; set; } public string? Error { get; set; }
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        public void Append(string text) { lock (_outputLock) { _output.Append(text); if (_output.Length > MaxOutputChars) _output.Remove(0, _output.Length - MaxOutputChars); } }
        public string ReadOutput() { lock (_outputLock) return _output.ToString(); }
        public void Dispose() { Cancellation.Cancel(); Kill(Process); Process?.Dispose(); Cancellation.Dispose(); }
    }
}
