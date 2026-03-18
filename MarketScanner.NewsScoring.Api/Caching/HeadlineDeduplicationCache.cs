using MarketScanner.NewsScoring.Api.Caching.Interfaces;
using MarketScanner.NewsScoring.Api.Models;
using Microsoft.Extensions.Caching.Memory;

namespace MarketScanner.NewsScoring.Api.Caching;

public sealed class HeadlineDeduplicationCache : IHeadlineDeduplicationCache
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);
    private readonly IMemoryCache _memoryCache;

    public HeadlineDeduplicationCache(IMemoryCache memoryCache)
    {
        _memoryCache = memoryCache;
    }

    public bool TryGet(string symbol, string headline, out NewsScoringResult? result)
    {
        return _memoryCache.TryGetValue(BuildKey(symbol, headline), out result);
    }

    public void Set(string symbol, string headline, NewsScoringResult result, TimeSpan ttl)
    {
        var effectiveTtl = ttl <= TimeSpan.Zero ? DefaultTtl : ttl;
        _memoryCache.Set(BuildKey(symbol, headline), result, effectiveTtl);
    }

    private static string BuildKey(string symbol, string headline)
    {
        return $"{symbol.Trim().ToUpperInvariant()}::{headline.Trim().ToLowerInvariant()}";
    }
}
