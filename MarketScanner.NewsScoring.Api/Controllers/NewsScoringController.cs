using MarketScanner.NewsScoring.Api.Models.Requests;
using MarketScanner.NewsScoring.Api.Models.Responses;
using MarketScanner.NewsScoring.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace MarketScanner.NewsScoring.Api.Controllers;

[ApiController]
[Route("api/news-scoring")]
public sealed class NewsScoringController : ControllerBase
{
    private readonly INewsScoringService _newsScoringService;

    public NewsScoringController(INewsScoringService newsScoringService)
    {
        _newsScoringService = newsScoringService;
    }

    [HttpPost("score")]
    public async Task<ActionResult<ScoreNewsResponse>> ScoreAsync(
        [FromBody] ScoreNewsRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol))
        {
            return BadRequest("Symbol is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Headline))
        {
            return BadRequest("Headline is required.");
        }

        var result = await _newsScoringService.ScoreAsync(request, cancellationToken);
        return Ok(result);
    }
}
