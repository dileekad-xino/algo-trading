namespace MarketScanner.Models;

public sealed record NewsHeadlineItem(
    string Symbol,
    string Headline,
    DateTime PublishedAtUtc,
    string ProviderCode,
    string? ArticleId = null
);
