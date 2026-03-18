using System.Text.Json;
using MarketScanner.NewsScoring.Api.Clients.Interfaces;
using MarketScanner.NewsScoring.Api.Models;
using MarketScanner.NewsScoring.Api.Models.Requests;
using MarketScanner.NewsScoring.Api.Providers.Interfaces;
using MarketScanner.NewsScoring.Api.Services;

namespace MarketScanner.NewsScoring.Api.Providers;

public sealed class OpenAiHeadlineScoringProvider : IHeadlineScoringProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IOpenAiClient _openAiClient;
    private readonly NewsPromptBuilder _newsPromptBuilder;
    private readonly ILogger<OpenAiHeadlineScoringProvider> _logger;

    public OpenAiHeadlineScoringProvider(
        IOpenAiClient openAiClient,
        NewsPromptBuilder newsPromptBuilder,
        ILogger<OpenAiHeadlineScoringProvider> logger)
    {
        _openAiClient = openAiClient;
        _newsPromptBuilder = newsPromptBuilder;
        _logger = logger;
    }

    public async Task<NewsScoringResult> ScoreAsync(ScoreNewsRequest request, CancellationToken cancellationToken = default)
    {
        var prompt = _newsPromptBuilder.BuildScoringPrompt(request);
        var rawResponse = await _openAiClient.CreateResponseAsync(prompt, cancellationToken);

        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            throw new InvalidOperationException("OpenAI response was empty.");
        }

        var parsedResult = TryParseStructuredResponse(rawResponse, request);
        if (parsedResult is not null)
        {
            return parsedResult;
        }

        _logger.LogInformation("OpenAI client returned a non-structured placeholder response for symbol {Symbol}.", request.Symbol);
        throw new InvalidOperationException("OpenAI response was not in a supported format.");
    }

    private static NewsScoringResult? TryParseStructuredResponse(string rawResponse, ScoreNewsRequest request)
    {
        try
        {
            var result = JsonSerializer.Deserialize<NewsScoringResult>(rawResponse, SerializerOptions);
            if (result is null)
            {
                return null;
            }

            result.Symbol = string.IsNullOrWhiteSpace(result.Symbol) ? request.Symbol : result.Symbol;
            result.Headline = string.IsNullOrWhiteSpace(result.Headline) ? request.Headline : result.Headline;
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
