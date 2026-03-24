using System.Collections.Concurrent;
using MarketScanner.Models;
using MarketScanner.Services.Ibkr;
using Microsoft.Extensions.Logging;

namespace MarketScanner.Services.Impl;

public sealed class NewsHeadlineService : INewsHeadlineService, IDisposable
{
    private static readonly TimeSpan RecentWindow = TimeSpan.FromHours(6);
    private const int MaxHeadlinesPerSymbol = 10;

    private readonly IbkrGatewayService _gatewayService;
    private readonly ILogger<NewsHeadlineService> _logger;
    private readonly object _syncLock = new();
    private readonly HashSet<string> _trackedSymbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SymbolHeadlineState> _headlineCache = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<NewsHeadlineItem>? HeadlineUpdated;

    public NewsHeadlineService(
        IbkrGatewayService gatewayService,
        ILogger<NewsHeadlineService> logger)
    {
        _gatewayService = gatewayService;
        _logger = logger;
        _gatewayService.NewsHeadlineReceived += OnGatewayHeadlineReceived;
    }

    public void SyncSymbols(IEnumerable<string> symbols)
    {
        var targetSymbols = symbols
            .Where(static symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(static symbol => symbol.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> symbolsToSubscribe;
        List<string> symbolsToUnsubscribe;

        lock (_syncLock)
        {
            symbolsToSubscribe = targetSymbols.Except(_trackedSymbols, StringComparer.OrdinalIgnoreCase).ToList();
            symbolsToUnsubscribe = _trackedSymbols.Except(targetSymbols, StringComparer.OrdinalIgnoreCase).ToList();

            _trackedSymbols.Clear();
            foreach (var symbol in targetSymbols)
            {
                _trackedSymbols.Add(symbol);
            }
        }

        foreach (var symbol in symbolsToSubscribe)
        {
            _ = SubscribeSymbolSafeAsync(symbol);
        }

        foreach (var symbol in symbolsToUnsubscribe)
        {
            _gatewayService.CancelNewsSubscription(symbol);
        }

        PruneAllExpiredHeadlines();
    }

    public bool HasRecentNews(string symbol)
    {
        var latest = GetLatestHeadline(symbol);
        return latest is not null;
    }

    public NewsHeadlineItem? GetLatestHeadline(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        if (!_headlineCache.TryGetValue(normalizedSymbol, out var state))
        {
            return null;
        }

        lock (state.Sync)
        {
            PruneExpiredHeadlines(state);
            return state.Items.FirstOrDefault();
        }
    }

    public IReadOnlyList<NewsHeadlineItem> GetRecentHeadlines(string symbol, int maxCount = 3)
    {
        if (string.IsNullOrWhiteSpace(symbol) || maxCount <= 0)
        {
            return Array.Empty<NewsHeadlineItem>();
        }

        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        if (!_headlineCache.TryGetValue(normalizedSymbol, out var state))
        {
            return Array.Empty<NewsHeadlineItem>();
        }

        lock (state.Sync)
        {
            PruneExpiredHeadlines(state);
            return state.Items.Take(maxCount).ToList();
        }
    }

    private async Task SubscribeSymbolSafeAsync(string symbol)
    {
        try
        {
            await _gatewayService.EnsureNewsSubscriptionAsync(symbol).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe to IBKR headlines for {Symbol}", symbol);
        }
    }

    private void OnGatewayHeadlineReceived(object? sender, NewsHeadlineItem headline)
    {
        if (string.IsNullOrWhiteSpace(headline.Symbol) || string.IsNullOrWhiteSpace(headline.Headline))
        {
            return;
        }

        var normalizedSymbol = headline.Symbol.Trim().ToUpperInvariant();
        var normalizedHeadline = headline with { Symbol = normalizedSymbol };

        var state = _headlineCache.GetOrAdd(normalizedSymbol, static _ => new SymbolHeadlineState());

        lock (state.Sync)
        {
            PruneExpiredHeadlines(state);

            var dedupeKey = BuildDedupeKey(normalizedHeadline);
            if (!state.DedupeKeys.Add(dedupeKey))
            {
                return;
            }

            state.Items.Insert(0, normalizedHeadline);
            if (state.Items.Count > MaxHeadlinesPerSymbol)
            {
                state.Items.RemoveRange(MaxHeadlinesPerSymbol, state.Items.Count - MaxHeadlinesPerSymbol);
            }

            state.DedupeKeys.Clear();
            foreach (var item in state.Items)
            {
                state.DedupeKeys.Add(BuildDedupeKey(item));
            }
        }

        HeadlineUpdated?.Invoke(this, normalizedHeadline);
    }

    private void PruneAllExpiredHeadlines()
    {
        foreach (var state in _headlineCache.Values)
        {
            lock (state.Sync)
            {
                PruneExpiredHeadlines(state);
            }
        }
    }

    private static void PruneExpiredHeadlines(SymbolHeadlineState state)
    {
        var cutoff = DateTime.UtcNow - RecentWindow;
        state.Items.RemoveAll(item => item.PublishedAtUtc < cutoff);
        state.DedupeKeys.Clear();
        foreach (var item in state.Items)
        {
            state.DedupeKeys.Add(BuildDedupeKey(item));
        }
    }

    private static string BuildDedupeKey(NewsHeadlineItem item)
    {
        return string.Join('|',
            item.Symbol,
            item.ProviderCode,
            item.ArticleId ?? string.Empty,
            item.Headline.Trim());
    }

    public void Dispose()
    {
        _gatewayService.NewsHeadlineReceived -= OnGatewayHeadlineReceived;
    }

    private sealed class SymbolHeadlineState
    {
        public object Sync { get; } = new();
        public List<NewsHeadlineItem> Items { get; } = new();
        public HashSet<string> DedupeKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
