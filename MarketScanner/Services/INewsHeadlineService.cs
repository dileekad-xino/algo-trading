using MarketScanner.Models;

namespace MarketScanner.Services;

public interface INewsHeadlineService
{
    event EventHandler<NewsHeadlineItem>? HeadlineUpdated;

    void SyncSymbols(IEnumerable<string> symbols);

    bool HasRecentNews(string symbol);

    NewsHeadlineItem? GetLatestHeadline(string symbol);

    IReadOnlyList<NewsHeadlineItem> GetRecentHeadlines(string symbol, int maxCount = 3);
}
