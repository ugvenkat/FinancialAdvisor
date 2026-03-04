using FinancialAdvisor.Models;
using FinancialAdvisor.Tools;
using Newtonsoft.Json.Linq;

namespace FinancialAdvisor.Agents;

/// <summary>
/// Agent 3: Sentiment Analysis Agent — TRUE AGENTIC
///
/// The LLM autonomously:
/// - Decides to fetch news from multiple sources
/// - Runs the classify_sentiment tool on the collected headlines
/// - Evaluates whether it has enough articles
/// - Synthesizes a comprehensive sentiment picture
/// </summary>
public class SentimentAnalysisAgent
{
    private readonly ReActEngine      _engine;
    private readonly FinancialToolkit _toolkit;
    private readonly ILogger<SentimentAnalysisAgent> _log;

    public SentimentAnalysisAgent(ReActEngine engine, FinancialToolkit toolkit,
        ILogger<SentimentAnalysisAgent> log)
    {
        _engine  = engine;
        _toolkit = toolkit;
        _log     = log;
    }

    public async Task<(SentimentScore Score, AgentTrace Trace)> AnalyzeAsync(
        string jobId, StockRawData data, CancellationToken ct = default)
    {
        _log.LogInformation("[SentimentAnalysisAgent][{Ticker}] Starting agentic sentiment analysis", data.Ticker);

        var tools = FinancialToolkit.AllTools
            .Where(t => new[] { "get_latest_news", "get_marketwatch_news",
                                 "classify_sentiment" }.Contains(t.Name))
            .ToList();

        var executors = _toolkit.GetExecutors();

        // Provide pre-collected headlines as context
        var existingHeadlines = data.News.Any()
            ? $"Pre-loaded headlines:\n{string.Join("\n", data.News.Take(5).Select(n => $"- {n.Title}"))}"
            : "No pre-loaded news available.";

        var goal = $"""
            Perform a comprehensive sentiment analysis for {data.Ticker} ({data.CompanyName}).

            {existingHeadlines}

            Your tasks:
            1. Fetch fresh news using get_latest_news for {data.Ticker}
            2. Fetch additional news from get_marketwatch_news for {data.Ticker}
            3. Combine ALL headlines into one list
            4. Run classify_sentiment on the combined headlines
            5. Analyze the results — what is the market's current mood?
            6. Are there specific themes driving sentiment? (earnings, product launches, macro concerns, etc.)

            In your FINAL ANSWER provide:
            - Overall sentiment: Bullish / Neutral / Bearish
            - Sentiment score: -1.0 (very bearish) to +1.0 (very bullish)
            - Bullish %, Neutral %, Bearish % breakdown
            - Top 3 bullish headlines (if any)
            - Top 3 bearish headlines (if any)
            - 2-sentence sentiment summary
            """;

        var trace = await _engine.RunAsync(
            jobId, "SentimentAnalysisAgent", data.Ticker, goal, tools, executors, ct);

        var score = ParseSentimentScore(data.Ticker, trace.FinalAnswer, data.News);
        score.Trace = trace;

        _log.LogInformation("[SentimentAnalysisAgent][{Ticker}] Sentiment: {Overall} ({Score:+0.00;-0.00}) in {Steps} steps",
            data.Ticker, score.Overall, score.Score, trace.TotalSteps);

        return (score, trace);
    }

    private static SentimentScore ParseSentimentScore(string ticker, string finalAnswer, List<NewsArticle> existingNews)
    {
        var score = new SentimentScore
        {
            Ticker           = ticker,
            SentimentSummary = finalAnswer
        };

        try
        {
            // Try JSON block first
            var jsonStart = finalAnswer.IndexOf('{');
            var jsonEnd   = finalAnswer.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var obj = JObject.Parse(finalAnswer[jsonStart..(jsonEnd + 1)]);
                score.Score          = obj["sentiment_score"]?.Value<double>() ?? obj["score"]?.Value<double>() ?? 0;
                score.BullishPercent = obj["bullish_pct"]?.Value<double>()  ?? obj["bullish_percent"]?.Value<double>() ?? 0;
                score.BearishPercent = obj["bearish_pct"]?.Value<double>()  ?? obj["bearish_percent"]?.Value<double>() ?? 0;
                score.NeutralPercent = obj["neutral_pct"]?.Value<double>()  ?? obj["neutral_percent"]?.Value<double>() ?? 0;

                var overallStr = obj["overall"]?.ToString() ?? obj["overall_sentiment"]?.ToString() ?? "";
                score.Overall = ParseSentimentClass(overallStr, score.Score);
            }
            else
            {
                // Parse from natural language
                ParseSentimentFromText(finalAnswer, score);
            }
        }
        catch
        {
            ParseSentimentFromText(finalAnswer, score);
        }

        // Populate items from existing news if available
        if (existingNews.Any() && !score.Items.Any())
        {
            foreach (var article in existingNews.Take(10))
            {
                var text  = article.Title.ToLowerInvariant();
                var bull  = new[] { "beat", "surge", "record", "upgrade", "buy", "growth", "profit", "gains" }.Count(w => text.Contains(w));
                var bear  = new[] { "miss", "loss", "decline", "downgrade", "warning", "cut", "fail", "drop" }.Count(w => text.Contains(w));
                var sent  = bull > bear ? SentimentClass.Bullish : bear > bull ? SentimentClass.Bearish : SentimentClass.Neutral;
                score.Items.Add(new SentimentItem { Source = article.Source, Headline = article.Title, Sentiment = sent, Confidence = 0.65 });
            }
        }

        return score;
    }

    private static void ParseSentimentFromText(string text, SentimentScore score)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("bullish") || lower.Contains("positive")) score.Score = 0.35;
        else if (lower.Contains("bearish") || lower.Contains("negative")) score.Score = -0.35;
        else score.Score = 0;

        score.Overall = score.Score switch { > 0.20 => SentimentClass.Bullish, < -0.20 => SentimentClass.Bearish, _ => SentimentClass.Neutral };

        // Try to extract percentages
        var pcts = System.Text.RegularExpressions.Regex.Matches(text, @"(\d+)%\s*(bullish|bearish|neutral)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in pcts)
        {
            if (double.TryParse(m.Groups[1].Value, out var pct))
            {
                var label = m.Groups[2].Value.ToLower();
                if (label == "bullish") score.BullishPercent = pct;
                else if (label == "bearish") score.BearishPercent = pct;
                else if (label == "neutral") score.NeutralPercent = pct;
            }
        }

        if (score.BullishPercent + score.BearishPercent + score.NeutralPercent == 0)
        {
            score.NeutralPercent = 100;
        }
    }

    private static SentimentClass ParseSentimentClass(string s, double numericScore) =>
        s.ToLower() switch
        {
            var v when v.Contains("bullish") || v.Contains("positive") => SentimentClass.Bullish,
            var v when v.Contains("bearish") || v.Contains("negative") => SentimentClass.Bearish,
            _ => numericScore switch { > 0.20 => SentimentClass.Bullish, < -0.20 => SentimentClass.Bearish, _ => SentimentClass.Neutral }
        };
}
