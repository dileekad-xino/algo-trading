using MarketScanner.NewsScoring.Api.Models;

namespace MarketScanner.NewsScoring.Api.Caching.Interfaces;

public interface IHeadlineDeduplicationCache
{
    bool TryGet(string symbol, string headline, out NewsScoringResult? result);

    void Set(string symbol, string headline, NewsScoringResult result, TimeSpan ttl);
}
