using MarketScanner.NewsScoring.Api.Caching;
using MarketScanner.NewsScoring.Api.Caching.Interfaces;
using MarketScanner.NewsScoring.Api.Clients;
using MarketScanner.NewsScoring.Api.Clients.Interfaces;
using MarketScanner.NewsScoring.Api.Options;
using MarketScanner.NewsScoring.Api.Providers;
using MarketScanner.NewsScoring.Api.Providers.Interfaces;
using MarketScanner.NewsScoring.Api.Services;
using MarketScanner.NewsScoring.Api.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddMemoryCache();
builder.Services.Configure<OpenAiOptions>(builder.Configuration.GetSection(OpenAiOptions.SectionName));
builder.Services.Configure<ResilienceOptions>(builder.Configuration.GetSection(ResilienceOptions.SectionName));
builder.Services.AddScoped<INewsScoringService, NewsScoringService>();
builder.Services.AddScoped<NewsPromptBuilder>();
builder.Services.AddScoped<IHeadlineScoringProvider, OpenAiHeadlineScoringProvider>();
builder.Services.AddSingleton<IHeadlineDeduplicationCache, HeadlineDeduplicationCache>();
builder.Services.AddHttpClient<IOpenAiClient, OpenAiClient>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
