// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OrchestratorIDE.Tools;

/// <summary>
/// Shared gate for the local studio integrations. A bearer token is only ever sent to a host
/// this classifies as local, so an IP literal must always be judged by <see cref="IPAddress"/>
/// rather than by the single-label shortcut.
/// </summary>
internal static class LocalIntegrationHost
{
    // ChatPanel re-registers the studio tools on every chat turn, so each Register call would
    // otherwise leak a connection pool. The handler is shared and never disposed by its clients.
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// <paramref name="bearerToken"/> is optional (hardcoreerik, 2026-08-01: his own local
    /// Art Forge Studio / ComfyUI / KeyHound instances run with no auth at all — a hobby-project
    /// front end for ComfyUI he wrote himself, not a service that issues device tokens) -- but
    /// ONLY for a genuinely loopback <paramref name="serviceUri"/> (CodeRabbit review, PR #100,
    /// CWE-306: <see cref="IsLocal"/> also admits private-IP and single-label LAN hosts, and a
    /// tokenless request to one of those could be reached by another machine on the same
    /// network, not just this one). Callers must still gate on <see cref="IsLocal"/> first for
    /// the broader local/private-IP/single-label check -- this is the stricter, auth-specific
    /// gate on top of it. Throws rather than silently sending an unauthenticated request to a
    /// non-loopback host, matching every other caller's existing "throw on a bad target"
    /// posture (e.g. ArtForgeTools.Register's non-local-URL check).
    /// </summary>
    public static HttpClient CreateClient(string? bearerToken, Uri serviceUri)
    {
        if (string.IsNullOrWhiteSpace(bearerToken) && !IsLoopback(serviceUri))
            throw new ArgumentException(
                $"'{serviceUri}' is not loopback -- a bearer token is required for private-IP/LAN hosts " +
                "(tokenless access is only safe when the service can only ever be reached from this machine).",
                nameof(bearerToken));

        var http = new HttpClient(SharedHandler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(2) };
        if (!string.IsNullOrWhiteSpace(bearerToken))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return http;
    }

    /// <summary>
    /// True only for 127.0.0.1/::1/localhost-shaped addresses -- the strict subset of
    /// <see cref="IsLocal"/> that <see cref="CreateClient"/> requires when no bearer token is
    /// configured. Deliberately excludes private-IP (10.x/192.168.x/172.16-31.x, IPv6
    /// unique-local) and single-label LAN hostnames, which <see cref="IsLocal"/> still permits
    /// for the token-configured case -- another machine on the same network could reach those.
    /// </summary>
    public static bool IsLoopback(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host)) return false;
        var host = uri.Host.Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip)) return IPAddress.IsLoopback(ip);
        return uri.IsLoopback;
    }

    public static bool IsLocal(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host)) return false;

        var host = uri.Host.Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var bytes = ip.GetAddressBytes();
            if (bytes.Length == 16) return (bytes[0] & 0xFE) == 0xFC; // unique-local fc00::/7
            return bytes[0] == 10
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
        }

        return uri.IsLoopback || !host.Contains('.');
    }

    /// <summary>
    /// Reads a string argument. Local models routinely emit <c>""</c> for optional parameters,
    /// so a blank value is treated as absent and yields the documented default.
    /// </summary>
    public static string String(Dictionary<string, object?> args, string key, string fallback = "")
    {
        if (!args.TryGetValue(key, out var value)) return fallback;
        var text = value switch
        {
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            JsonElement json when json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value?.ToString(),
        };
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }
}
