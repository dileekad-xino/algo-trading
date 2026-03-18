using MarketScanner.NewsScoring.Api.Models;
using MarketScanner.NewsScoring.Api.Models.Requests;

namespace MarketScanner.NewsScoring.Api.Providers.Interfaces;

public interface IHeadlineScoringProvider
{
    Task<NewsScoringResult> ScoreAsync(ScoreNewsRequest request, CancellationToken cancellationToken = default);
}
