using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarketScanner.NewsScoring.Api.Clients.Interfaces;
using MarketScanner.NewsScoring.Api.Options;
using Microsoft.Extensions.Options;

namespace MarketScanner.NewsScoring.Api.Clients;

public sealed class OpenAiClient : IOpenAiClient
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiOptions _options;
    private readonly ILogger<OpenAiClient> _logger;

    public OpenAiClient(
        HttpClient httpClient,
        IOptions<OpenAiOptions> options,
        ILogger<OpenAiClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> CreateResponseAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) ||
            string.IsNullOrWhiteSpace(_options.BaseUrl) ||
            string.IsNullOrWhiteSpace(_options.Model))
        {
            _logger.LogInformation("OpenAI client is not configured. Skipping external API call.");
            return string.Empty;
        }

        if (!Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("OpenAI base URL is invalid.");
        }

        _httpClient.BaseAddress = baseUri;
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = _options.Model,
                    input = prompt
                }),
                Encoding.UTF8,
                "application/json")
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenAI request failed with status code {StatusCode}.", (int)response.StatusCode);
            throw new HttpRequestException($"OpenAI request failed with status code {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
