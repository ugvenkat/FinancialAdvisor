using FinancialAdvisor.Models;
using FinancialAdvisor.Tools;
using Newtonsoft.Json.Linq;

namespace FinancialAdvisor.Agents;

/// <summary>
/// Agent 2: Fundamental Analysis Agent — TRUE AGENTIC
///
/// The LLM reasons about which metrics matter most for THIS specific company,
/// decides to run the scoring tool, interprets the results, and can go back
/// to fetch more data if something is missing — all autonomously.
/// </summary>
public class FundamentalAnalysisAgent
{
    private readonly ReActEngine      _engine;
    private readonly FinancialToolkit _toolkit;
    private readonly ILogger<FundamentalAnalysisAgent> _log;

    public FundamentalAnalysisAgent(ReActEngine engine, FinancialToolkit toolkit,
        ILogger<FundamentalAnalysisAgent> log)
    {
        _engine  = engine;
        _toolkit = toolkit;
        _log     = log;
    }

    public async Task<(FundamentalScore Score, AgentTrace Trace)> AnalyzeAsync(
        string jobId, StockRawData data, CancellationToken ct = default)
    {
        _log.LogInformation("[FundamentalAnalysisAgent][{Ticker}] Starting agentic fundamental analysis", data.Ticker);

        // This agent can fetch MORE data if needed, and run the scoring tool
        var tools = FinancialToolkit.AllTools
            .Where(t => new[] { "get_financial_ratios", "get_stock_price",
                                 "calculate_fundamental_score" }.Contains(t.Name))
            .ToList();

        var executors = _toolkit.GetExecutors();

        var metricsContext = $"""
            Pre-loaded data for {data.Ticker}:
            - Current Price: ${data.Metrics.CurrentPrice:F2}
            - P/E Ratio: {data.Metrics.PERatio:F1}
            - P/B Ratio: {data.Metrics.PBRatio:F1}
            - EPS: ${data.Metrics.EPS:F2}
            - EPS Growth YoY: {data.Metrics.EPSGrowthYoY:P1}
            - Revenue Growth YoY: {data.Metrics.RevenueGrowthYoY:P1}
            - Debt/Equity: {data.Metrics.DebtToEquity:F2}
            - Free Cash Flow: ${((double?)data.Metrics.FreeCashFlow ?? 0) / 1e9:F1}B
            - Market Cap: ${((double?)data.Metrics.MarketCap ?? 0) / 1e9:F1}B
            - Sector: {data.Metrics.Sector}
            """;

        var goal = $"""
            Perform a deep fundamental analysis of {data.Ticker} ({data.CompanyName}).

            {metricsContext}

            Your tasks:
            1. If any key metrics are missing (null/N/A), use get_financial_ratios or get_stock_price to fetch them
            2. Run calculate_fundamental_score with ALL available metrics
            3. Interpret the score — what does it mean for this specific company?
            4. Consider the sector context — is this P/E reasonable for this industry?
            5. Identify the top 3 strengths and top 3 weaknesses

            In your FINAL ANSWER provide:
            - Fundamental score and grade
            - Top strengths (with data points)
            - Top weaknesses (with data points)
            - Your analytical conclusion (2-3 sentences)
            """;

        var trace = await _engine.RunAsync(
            jobId, "FundamentalAnalysisAgent", data.Ticker, goal, tools, executors, ct);

        var score = ParseFundamentalScore(data.Ticker, trace.FinalAnswer);
        score.Trace = trace;

        _log.LogInformation("[FundamentalAnalysisAgent][{Ticker}] Score: {Score}/100 Grade: {Grade} in {Steps} steps",
            data.Ticker, score.Score, score.Grade, trace.TotalSteps);

        return (score, trace);
    }

    private static FundamentalScore ParseFundamentalScore(string ticker, string finalAnswer)
    {
        var score = new FundamentalScore { Ticker = ticker, DetailedAnalysis = finalAnswer };

        try
        {
            // Try to extract score from JSON block
            var jsonStart = finalAnswer.IndexOf('{');
            var jsonEnd   = finalAnswer.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var obj = JObject.Parse(finalAnswer[jsonStart..(jsonEnd + 1)]);
                score.Score = obj["score"]?.Value<double>() ?? 50;
                score.Grade = obj["grade"]?.ToString() ?? GradeFromScore(score.Score);

                var s = obj["strengths"] as JArray;
                if (s != null) score.Strengths = s.Select(x => x.ToString()).ToList();

                var w = obj["weaknesses"] as JArray;
                if (w != null) score.Weaknesses = w.Select(x => x.ToString()).ToList();
            }
            else
            {
                // Parse from text
                ExtractScoreFromText(finalAnswer, score);
            }
        }
        catch
        {
            ExtractScoreFromText(finalAnswer, score);
        }

        // Ensure grade is set
        if (string.IsNullOrEmpty(score.Grade))
            score.Grade = GradeFromScore(score.Score);

        return score;
    }

    private static void ExtractScoreFromText(string text, FundamentalScore score)
    {
        // Try to find "score: 72" or "72/100" patterns
        var scoreMatch = System.Text.RegularExpressions.Regex.Match(text,
            @"(?:score[:\s]+|score of\s+)(\d+(?:\.\d+)?)(?:/100)?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (scoreMatch.Success && double.TryParse(scoreMatch.Groups[1].Value, out var s))
            score.Score = s;
        else
            score.Score = 50;

        var gradeMatch = System.Text.RegularExpressions.Regex.Match(text,
            @"grade[:\s]+([A-F][+-]?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        score.Grade = gradeMatch.Success ? gradeMatch.Groups[1].Value.ToUpper() : GradeFromScore(score.Score);

        // Extract bullet points as strengths/weaknesses
        var lines = text.Split('\n').Select(l => l.Trim().TrimStart('-', '•', '*', ' ')).ToList();
        var inStrengths  = false;
        var inWeaknesses = false;
        foreach (var line in lines)
        {
            if (line.ToLower().Contains("strength")) { inStrengths = true; inWeaknesses = false; continue; }
            if (line.ToLower().Contains("weakness") || line.ToLower().Contains("risk")) { inWeaknesses = true; inStrengths = false; continue; }
            if (inStrengths  && line.Length > 10) score.Strengths.Add(line);
            if (inWeaknesses && line.Length > 10) score.Weaknesses.Add(line);
        }
    }

    private static string GradeFromScore(double s) =>
        s switch { >= 85 => "A", >= 70 => "B", >= 55 => "C", >= 40 => "D", _ => "F" };
}
