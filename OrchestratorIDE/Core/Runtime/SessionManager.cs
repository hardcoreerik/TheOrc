// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace OrchestratorIDE.Core.Runtime;

public sealed record RuntimeSessionLoadResult(
    bool Success,
    RuntimeRole Role,
    RuntimeRoleBinding? Binding,
    ModelLoadResult? ModelLoad,
    bool ReusedExistingSession,
    string? Message = null);

public sealed record RuntimeSessionSnapshot(
    RuntimeRole? Role,
    RuntimeRoleBinding? Binding,
    ModelLoadResult? LastLoad,
    RuntimeHealth Health,
    RuntimeStats Stats,
    bool HasPendingAdapter);

/// <summary>
/// Coordinates a persistent local runtime session for Native Runtime Phase 3.
/// This layer keeps the resolved base GGUF loaded instead of forcing callers to
/// reload per request. Adapter application is intentionally deferred to the
/// later AdapterManager/hot-swap spike; role adapters discovered by ModelDepot
/// are surfaced as pending metadata only.
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly ILocalModelRuntime _runtime;
    private readonly bool _disposeRuntime;
    private readonly TimeSpan _disposeWaitTimeout;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private RuntimeRole? _currentRole;
    private RuntimeRoleBinding? _currentBinding;
    private RuntimeOptions? _currentOptions;
    private ModelLoadResult? _lastLoad;
    private bool _disposing;
    private bool _disposed;

    public SessionManager(
        ILocalModelRuntime runtime,
        bool disposeRuntime = false,
        TimeSpan? disposeWaitTimeout = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _disposeRuntime = disposeRuntime;
        _disposeWaitTimeout = disposeWaitTimeout ?? TimeSpan.FromSeconds(10);
    }

    public RuntimeRole? CurrentRole
    {
        get
        {
            ThrowIfDisposed();
            return _currentRole;
        }
    }

    public RuntimeRoleBinding? CurrentBinding
    {
        get
        {
            ThrowIfDisposed();
            return _currentBinding;
        }
    }

    public ModelLoadResult? LastLoad
    {
        get
        {
            ThrowIfDisposed();
            return _lastLoad;
        }
    }

    public RuntimeHealth GetHealth()
    {
        ThrowIfDisposed();
        return _runtime.GetHealth();
    }

    public RuntimeStats GetStats()
    {
        ThrowIfDisposed();
        return _runtime.GetStats();
    }

    public RuntimeSessionSnapshot GetSnapshot()
    {
        ThrowIfDisposed();
        return new RuntimeSessionSnapshot(
            Role: _currentRole,
            Binding: _currentBinding,
            LastLoad: _lastLoad,
            Health: _runtime.GetHealth(),
            Stats: _runtime.GetStats(),
            HasPendingAdapter: _currentBinding?.Adapter is not null);
    }

    public async Task<RuntimeSessionLoadResult> LoadRoleAsync(
        ModelDepot depot,
        RuntimeRole role,
        RuntimeOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(depot);
        ThrowIfDisposed();

        var binding = depot.ResolveRole(role);
        if (binding is null)
        {
            var failure = new ModelLoadResult(
                false,
                _runtime.RuntimeName,
                role.ToString(),
                $"No base GGUF resolved for runtime role {role}.");
            return new RuntimeSessionLoadResult(false, role, null, failure, false, failure.Message);
        }

        return await LoadBindingAsync(binding, options, ct).ConfigureAwait(false);
    }

    public async Task<RuntimeSessionLoadResult> LoadBindingAsync(
        RuntimeRoleBinding binding,
        RuntimeOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ThrowIfDisposed();

        options ??= new RuntimeOptions();
        var acquired = false;

        try
        {
            await _loadGate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            ThrowIfDisposed();

            var health = _runtime.GetHealth();
            if (CanReuseCurrentSession(binding, options, health))
            {
                _currentRole = binding.Role;
                _currentBinding = binding;
                return new RuntimeSessionLoadResult(
                    true,
                    binding.Role,
                    binding,
                    _lastLoad,
                    ReusedExistingSession: true,
                    Message: "Base model already loaded.");
            }

            var load = await _runtime.LoadModelAsync(
                binding.BaseModel.Path,
                adapterPath: null,
                options,
                ct).ConfigureAwait(false);

            _lastLoad = load;

            if (_disposed)
            {
                return new RuntimeSessionLoadResult(
                    false,
                    binding.Role,
                    binding,
                    load,
                    ReusedExistingSession: false,
                    Message: "Session manager disposed during model load.");
            }

            if (!load.Success)
            {
                ClearCurrentSession();
                return new RuntimeSessionLoadResult(
                    false,
                    binding.Role,
                    binding,
                    load,
                    ReusedExistingSession: false,
                    Message: load.Message);
            }

            _currentRole = binding.Role;
            _currentBinding = binding;
            _currentOptions = options;

            return new RuntimeSessionLoadResult(
                true,
                binding.Role,
                binding,
                load,
                ReusedExistingSession: false,
                Message: binding.Adapter is null
                    ? load.Message
                    : "Base model loaded; adapter is pending AdapterManager support.");
        }
        finally
        {
            if (acquired)
                _loadGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed || _disposing)
            return;

        _disposing = true;
        var acquired = false;
        try
        {
            acquired = await _loadGate.WaitAsync(_disposeWaitTimeout).ConfigureAwait(false);

            _disposed = true;
            ClearCurrentSession();

            if (acquired && _disposeRuntime)
                await _runtime.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
                _loadGate.Release();
        }
    }

    private bool CanReuseCurrentSession(
        RuntimeRoleBinding binding,
        RuntimeOptions options,
        RuntimeHealth health)
    {
        if (_currentBinding is null || _currentOptions is null || _lastLoad?.Success != true)
            return false;

        if (!health.IsAvailable)
            return false;

        if (!StringComparer.OrdinalIgnoreCase.Equals(_currentBinding.BaseModel.Path, binding.BaseModel.Path))
            return false;

        return _currentOptions == options;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed || _disposing)
            throw new ObjectDisposedException(nameof(SessionManager));
    }

    private void ClearCurrentSession()
    {
        _currentRole = null;
        _currentBinding = null;
        _currentOptions = null;
    }
}
