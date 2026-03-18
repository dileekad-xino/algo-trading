using MarketScanner.NewsScoring.Api.Caching.Interfaces;
using MarketScanner.NewsScoring.Api.Models;
using MarketScanner.NewsScoring.Api.Models.Requests;
using MarketScanner.NewsScoring.Api.Models.Responses;
using MarketScanner.NewsScoring.Api.Providers.Interfaces;
using MarketScanner.NewsScoring.Api.Services.Interfaces;

namespace MarketScanner.NewsScoring.Api.Services;

public sealed class NewsScoringService : INewsScoringService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly IHeadlineScoringProvider _headlineScoringProvider;
    private readonly IHeadlineDeduplicationCache _headlineDeduplicationCache;
    private readonly ILogger<NewsScoringService> _logger;

    public NewsScoringService(
        IHeadlineScoringProvider headlineScoringProvider,
        IHeadlineDeduplicationCache headlineDeduplicationCache,
        ILogger<NewsScoringService> logger)
    {
        _headlineScoringProvider = headlineScoringProvider;
        _headlineDeduplicationCache = headlineDeduplicationCache;
        _logger = logger;
    }

    public async Task<ScoreNewsResponse> ScoreAsync(ScoreNewsRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedRequest = NormalizeRequest(request);

        if (_headlineDeduplicationCache.TryGet(normalizedRequest.Symbol, normalizedRequest.Headline, out var cachedResult) &&
            cachedResult is not null)
        {
            return MapToResponse(SanitizeResult(cachedResult, normalizedRequest));
        }

        NewsScoringResult result;

        try
        {
            var providerResult = await _headlineScoringProvider.ScoreAsync(normalizedRequest, cancellationToken);
            result = IsUsable(providerResult, normalizedRequest)
                ? SanitizeResult(providerResult, normalizedRequest)
                : BuildFallbackResult(normalizedRequest);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "External headline scoring failed for symbol {Symbol}. Falling back to local scoring.", normalizedRequest.Symbol);
            result = BuildFallbackResult(normalizedRequest);
        }

        _headlineDeduplicationCache.Set(normalizedRequest.Symbol, normalizedRequest.Headline, result, CacheTtl);
        return MapToResponse(result);
    }

    private static ScoreNewsRequest NormalizeRequest(ScoreNewsRequest request)
    {
        return new ScoreNewsRequest
        {
            Symbol = request.Symbol.Trim().ToUpperInvariant(),
            Headline = request.Headline.Trim(),
            CompanyName = request.CompanyName?.Trim(),
            LastPrice = request.LastPrice,
            MarketCap = request.MarketCap,
            AverageVolume = request.AverageVolume,
            Sector = request.Sector?.Trim(),
            Source = request.Source?.Trim(),
            PublishedAt = request.PublishedAt
        };
    }

    private static bool IsUsable(NewsScoringResult result, ScoreNewsRequest request)
    {
        return !string.IsNullOrWhiteSpace(result.Symbol) &&
               !string.IsNullOrWhiteSpace(result.Headline) &&
               string.Equals(result.Symbol.Trim(), request.Symbol, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(result.Headline.Trim(), request.Headline, StringComparison.Ordinal);
    }

    private static NewsScoringResult BuildFallbackResult(ScoreNewsRequest request)
    {
        var normalizedHeadline = request.Headline.ToLowerInvariant();
        var result = CreateResult(
            request,
            20,
            "Neutral",
            "GeneralUpdate",
            "Low",
            "Low",
            0.55d,
            "Monitor",
            "No clearly material catalyst detected from the headline.");

        if (ContainsAny(normalizedHeadline, "contract", "agreement"))
        {
            result = CreateResult(
                request,
                62,
                "Bullish",
                "CommercialAgreement",
                "Medium",
                "Medium",
                0.72d,
                "ShortTerm",
                "The headline suggests a commercial agreement that may support revenue visibility.");
        }

        if (ContainsAny(normalizedHeadline, "$1 billion", "$1b", "1 billion"))
        {
            result = CreateResult(
                request,
                88,
                "Bullish",
                "MajorCommercialAgreement",
                "High",
                "High",
                0.9d,
                "Swing",
                "The headline references a billion-dollar scale event, which implies high materiality.");
        }

        if (ContainsAny(normalizedHeadline, "offering", "dilution"))
        {
            result = CreateResult(
                request,
                76,
                "Bearish",
                "CapitalRaise",
                "High",
                "High",
                0.87d,
                "ShortTerm",
                "The headline indicates a likely dilution event, which is typically negative for common shareholders.");
        }

        return result;
    }

    private static NewsScoringResult CreateResult(
        ScoreNewsRequest request,
        int significanceScore,
        string sentiment,
        string eventType,
        string materiality,
        string urgency,
        double confidence,
        string tradeHorizon,
        string reasoningShort)
    {
        return new NewsScoringResult
        {
            Symbol = request.Symbol,
            Headline = request.Headline,
            SignificanceScore = significanceScore,
            Sentiment = sentiment,
            EventType = eventType,
            Materiality = materiality,
            Urgency = urgency,
            Confidence = confidence,
            TradeHorizon = tradeHorizon,
            ReasoningShort = reasoningShort
        };
    }

    private static NewsScoringResult SanitizeResult(NewsScoringResult result, ScoreNewsRequest request)
    {
        return new NewsScoringResult
        {
            Symbol = string.IsNullOrWhiteSpace(result.Symbol) ? request.Symbol : result.Symbol.Trim().ToUpperInvariant(),
            Headline = string.IsNullOrWhiteSpace(result.Headline) ? request.Headline : result.Headline.Trim(),
            SignificanceScore = Math.Clamp(result.SignificanceScore, 0, 100),
            Sentiment = SanitizeString(result.Sentiment, "Neutral"),
            EventType = SanitizeString(result.EventType, "GeneralUpdate"),
            Materiality = SanitizeString(result.Materiality, "Low"),
            Urgency = SanitizeString(result.Urgency, "Low"),
            Confidence = Math.Clamp(result.Confidence, 0d, 1d),
            TradeHorizon = SanitizeString(result.TradeHorizon, "Monitor"),
            ReasoningShort = SanitizeString(result.ReasoningShort, "No clearly material catalyst detected from the headline.")
        };
    }

    private static ScoreNewsResponse MapToResponse(NewsScoringResult result)
    {
        return new ScoreNewsResponse
        {
            Symbol = result.Symbol,
            Headline = result.Headline,
            SignificanceScore = result.SignificanceScore,
            Sentiment = result.Sentiment,
            EventType = result.EventType,
            Materiality = result.Materiality,
            Urgency = result.Urgency,
            Confidence = result.Confidence,
            TradeHorizon = result.TradeHorizon,
            ReasoningShort = result.ReasoningShort
        };
    }

    private static string SanitizeString(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (value.Contains(candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
