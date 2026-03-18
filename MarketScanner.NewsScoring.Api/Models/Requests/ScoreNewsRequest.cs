namespace MarketScanner.NewsScoring.Api.Models.Requests;

public sealed class ScoreNewsRequest
{
    public string Symbol { get; set; } = string.Empty;

    public string Headline { get; set; } = string.Empty;

    public string? CompanyName { get; set; }

    public decimal? LastPrice { get; set; }

    public decimal? MarketCap { get; set; }

    public long? AverageVolume { get; set; }

    public string? Sector { get; set; }

    public string? Source { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
}
