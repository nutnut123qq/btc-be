using Backend.Services.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Backend.Services;

public sealed class DataAuditCache(IMemoryCache cache)
{
    private static string Key(string symbol, bool includeInventory) =>
        $"data-audit:{symbol.Trim().ToUpperInvariant()}:{includeInventory}";
    public bool TryGet(string symbol, bool includeInventory, out DataAuditResponse? value) =>
        cache.TryGetValue(Key(symbol, includeInventory), out value);
    public void Set(string symbol, bool includeInventory, DataAuditResponse value) =>
        cache.Set(Key(symbol, includeInventory), value, TimeSpan.FromMinutes(5));
    public void Invalidate(string symbol)
    {
        cache.Remove(Key(symbol, false));
        cache.Remove(Key(symbol, true));
    }
}
