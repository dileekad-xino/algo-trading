namespace MarketScanner.NewsScoring.Api.Options;

public sealed class ResilienceOptions
{
    public const string SectionName = "Resilience";

    public int RetryCount { get; set; } = 2;

    public int BaseDelayMilliseconds { get; set; } = 300;
}
