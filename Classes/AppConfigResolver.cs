using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Fetches a central app-urls.json on startup, resolves this app's
/// backend URL by identifier, and caches it for the session.
/// Falls back to a hardcoded URL if the endpoint is unreachable.
/// </summary>
public static class AppConfigResolver
{
    // -------------------------------------------------------------------------
    // Configure these two values per-project before use
    // -------------------------------------------------------------------------

    /// <summary>URL to the shared config JSON (same for all apps).</summary>
    private const string ConfigEndpoint = "https://dx.dmt.ovh/sentryurls";

    /// <summary>This app's key inside the JSON (unique per project).</summary>
    private const string AppIdentifier = "updateserver"; // e.g. "nerv-agent", "sa-launcher"

    /// <summary>Used if the endpoint is unreachable or the key is missing.</summary>
    private const string FallbackUrl = "https://files.rusticaland.com";

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the resolved URL for this app.
    /// Always call InitAsync() first — this throws if init was skipped.
    /// </summary>
    public static string Url
    {
        get
        {
            if (!_initialized)
                throw new InvalidOperationException(
                    "AppConfigResolver.InitAsync() must be called before accessing Url.");
            return _resolvedUrl;
        }
    }

    /// <summary>True if the URL was resolved from the remote config (not the fallback).</summary>
    public static bool IsFromRemote { get; private set; }

    /// <summary>
    /// Fetches the config endpoint, resolves this app's URL, and caches it.
    /// Safe to call multiple times — subsequent calls are no-ops.
    /// </summary>
    /// <param name="timeoutSeconds">HTTP timeout in seconds (default: 5).</param>
    public static async Task InitAsync(int timeoutSeconds = 5)
    {
        if (_initialized) return;

        await _lock.WaitAsync();
        try
        {
            if (_initialized) return; // double-check after acquiring lock

            _resolvedUrl = await ResolveUrlAsync(timeoutSeconds);
            _initialized = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private static string _resolvedUrl = FallbackUrl;
    private static bool _initialized;
    private static readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

    private static readonly HttpClient _http = new HttpClient();

    private static async Task<string> ResolveUrlAsync(int timeoutSeconds)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            var json = await _http.GetStringAsync(ConfigEndpoint);

            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (map != null && map.TryGetValue(AppIdentifier, out var url) && !string.IsNullOrWhiteSpace(url))
            {
                IsFromRemote = true;
                return url.TrimEnd('/');
            }

            // Key not found in the map — use fallback
            return FallbackUrl;
        }
        catch
        {
            // Endpoint unreachable, JSON malformed, timeout, etc. — use fallback silently
            return FallbackUrl;
        }
    }
}
