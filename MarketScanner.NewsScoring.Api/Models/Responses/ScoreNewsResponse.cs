namespace MarketScanner.NewsScoring.Api.Models.Responses;

public sealed class ScoreNewsResponse
{
    public string Symbol { get; set; } = string.Empty;

    public string Headline { get; set; } = string.Empty;

    public int SignificanceScore { get; set; }

    public string Sentiment { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public string Materiality { get; set; } = string.Empty;

    public string Urgency { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public string TradeHorizon { get; set; } = string.Empty;

    public string ReasoningShort { get; set; } = string.Empty;
}
