using System.Collections.Concurrent;

namespace VortexBird.API;

/// <summary>Per-process protection; the gateway owns distributed production quotas.</summary>
public sealed class PartnerQuota
{
    private sealed class Window { public long Minute; public int Count; }
    private readonly ConcurrentDictionary<string, Window> windows = new();
    public bool TryAcquire(string key, int limit)
    {
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var window = windows.GetOrAdd(key, _ => new Window { Minute = minute });
        lock (window)
        {
            if (window.Minute != minute) { window.Minute = minute; window.Count = 0; }
            if (window.Count >= Math.Clamp(limit, 1, 100000)) return false;
            window.Count++;
            return true;
        }
    }
}
