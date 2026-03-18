namespace MarketScanner.NewsScoring.Api.Options;

public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAI";

    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://api.openai.com";

    public string Model { get; set; } = "gpt-5-mini";

    public int TimeoutSeconds { get; set; } = 15;
}
