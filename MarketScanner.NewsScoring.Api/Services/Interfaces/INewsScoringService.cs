using MarketScanner.NewsScoring.Api.Models.Requests;
using MarketScanner.NewsScoring.Api.Models.Responses;

namespace MarketScanner.NewsScoring.Api.Services.Interfaces;

public interface INewsScoringService
{
    Task<ScoreNewsResponse> ScoreAsync(ScoreNewsRequest request, CancellationToken cancellationToken = default);
}
