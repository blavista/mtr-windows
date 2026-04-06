using System.Collections.Concurrent;
using System.Net;

namespace MtrWindows;

/// <summary>
/// Background reverse-DNS resolver with a simple in-memory cache.
/// Lookups that are already in-flight or completed are not duplicated.
/// </summary>
internal sealed class DnsResolver
{
    // Cache: IP string → hostname (or IP string if lookup failed)
    private readonly ConcurrentDictionary<string, string> _cache  = new();
    private readonly ConcurrentDictionary<string, byte>   _inflight = new();

    private readonly bool _enabled;

    public DnsResolver(bool enabled = true) => _enabled = enabled;

    /// <summary>
    /// Asynchronously resolves <paramref name="address"/> and calls
    /// <paramref name="callback"/> with the hostname on the thread-pool.
    /// Silently skips if a lookup is already in-flight or cached.
    /// </summary>
    public void Resolve(IPAddress address, Action<string> callback)
    {
        if (!_enabled) return;

        string key = address.ToString();

        if (_cache.TryGetValue(key, out string? cached))
        {
            callback(cached);
            return;
        }

        // Guard against duplicate in-flight lookups
        if (!_inflight.TryAdd(key, 0)) return;

        _ = Task.Run(async () =>
        {
            string result;
            try
            {
                IPHostEntry entry = await Dns.GetHostEntryAsync(address).ConfigureAwait(false);
                result = string.IsNullOrEmpty(entry.HostName) ? key : entry.HostName;
            }
            catch
            {
                result = key; // fall back to the IP string
            }
            finally
            {
                _inflight.TryRemove(key, out _);
            }

            _cache[key] = result;
            callback(result);
        });
    }

    public void Toggle() => throw new NotSupportedException(
        "Create a new DnsResolver with enabled toggled.");
}
