using System.Text;
using MarketScanner.NewsScoring.Api.Models.Requests;

namespace MarketScanner.NewsScoring.Api.Services;

public sealed class NewsPromptBuilder
{
    public string BuildScoringPrompt(ScoreNewsRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Score this market news headline for trading significance.");
        builder.AppendLine($"Symbol: {request.Symbol}");
        builder.AppendLine($"Headline: {request.Headline}");
        builder.AppendLine($"CompanyName: {request.CompanyName ?? "n/a"}");
        builder.AppendLine($"LastPrice: {request.LastPrice?.ToString() ?? "n/a"}");
        builder.AppendLine($"MarketCap: {request.MarketCap?.ToString() ?? "n/a"}");
        builder.AppendLine($"AverageVolume: {request.AverageVolume?.ToString() ?? "n/a"}");
        builder.AppendLine($"Sector: {request.Sector ?? "n/a"}");
        builder.AppendLine($"Source: {request.Source ?? "n/a"}");
        builder.AppendLine($"PublishedAt: {request.PublishedAt?.ToString("O") ?? "n/a"}");
        builder.Append("Return significance, sentiment, event type, materiality, urgency, confidence, trade horizon, and concise reasoning.");
        return builder.ToString();
    }
}
