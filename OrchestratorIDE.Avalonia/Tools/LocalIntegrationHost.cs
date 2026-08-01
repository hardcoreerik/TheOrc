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

    public static HttpClient CreateClient(string bearerToken)
    {
        var http = new HttpClient(SharedHandler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return http;
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
