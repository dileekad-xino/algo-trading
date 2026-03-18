namespace MarketScanner.NewsScoring.Api.Clients.Interfaces;

public interface IOpenAiClient
{
    Task<string> CreateResponseAsync(string prompt, CancellationToken cancellationToken = default);
}
