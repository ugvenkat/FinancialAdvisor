using FinancialAdvisor.Models;
using FinancialAdvisor.Tools;
using Newtonsoft.Json.Linq;

namespace FinancialAdvisor.Agents;

/// <summary>
/// Agent 4: Risk Analysis Agent — TRUE AGENTIC
///
/// The LLM reasons about which risk factors are most relevant for this company,
/// fetches missing data if needed, runs the risk scoring tool, and forms
/// its own risk assessment narrative.
/// </summary>
public class RiskAnalysisAgent
{
    private readonly ReActEngine      _engine;
    private readonly FinancialToolkit _toolkit;
    private readonly ILogger<RiskAnalysisAgent> _log;

    public RiskAnalysisAgent(ReActEngine engine, FinancialToolkit toolkit,
        ILogger<RiskAnalysisAgent> log)
    {
        _engine  = engine;
        _toolkit = toolkit;
        _log     = log;
    }

    public async Task<(RiskScore Score, AgentTrace Trace)> AnalyzeAsync(
        string jobId, StockRawData data, CancellationToken ct = default)
    {
        _log.LogInformation("[RiskAnalysisAgent][{Ticker}] Starting agentic risk analysis", data.Ticker);

        var tools = FinancialToolkit.AllTools
            .Where(t => new[] { "get_stock_price", "get_financial_ratios",
                                 "calculate_risk_score" }.Contains(t.Name))
            .ToList();

        var executors = _toolkit.GetExecutors();

        var context = $"""
            Pre-loaded data for {data.Ticker}:
            - Beta: {data.Metrics.Beta:F2}
            - P/E Ratio: {data.Metrics.PERatio:F1}
            - Debt/Equity: {data.Metrics.DebtToEquity:F2}
            - Current Price: ${data.Metrics.CurrentPrice:F2}
            - 52W High: ${data.Metrics.FiftyTwoWeekHigh:F2}
            - 52W Low: ${data.Metrics.FiftyTwoWeekLow:F2}
            - Sector: {data.Metrics.Sector}
            - Free Cash Flow: ${((double?)data.Metrics.FreeCashFlow ?? 0) / 1e9:F1}B
            """;

        var goal = $"""
            Perform a comprehensive risk analysis for {data.Ticker} ({data.CompanyName}).

            {context}

            Your tasks:
            1. If beta, P/E, or debt/equity are missing, use get_stock_price or get_financial_ratios to fetch them
            2. Run calculate_risk_score with all available metrics
            3. Consider ALL risk dimensions:
               - Market risk (beta, volatility)
               - Valuation risk (is it overpriced?)
               - Financial risk (debt levels, cash flow sustainability)
               - Sector/industry risk
               - Technical risk (position relative to 52-week range)
            4. Identify the most significant risk factors specific to this company

            In your FINAL ANSWER provide:
            - Risk score (0-100, higher = riskier)
            - Risk level: Low / Medium / High / VeryHigh
            - Top 3-5 specific risk factors (with data)
            - 2-sentence risk summary
            """;

        var trace = await _engine.RunAsync(
            jobId, "RiskAnalysisAgent", data.Ticker, goal, tools, executors, ct);

        var score = ParseRiskScore(data.Ticker, trace.FinalAnswer);
        score.Trace = trace;

        _log.LogInformation("[RiskAnalysisAgent][{Ticker}] Risk: {Level} ({Score:F0}/100) in {Steps} steps",
            data.Ticker, score.Level, score.Score, trace.TotalSteps);

        return (score, trace);
    }

    private static RiskScore ParseRiskScore(string ticker, string finalAnswer)
    {
        var score = new RiskScore { Ticker = ticker, RiskSummary = finalAnswer };

        try
        {
            var jsonStart = finalAnswer.IndexOf('{');
            var jsonEnd   = finalAnswer.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var obj = JObject.Parse(finalAnswer[jsonStart..(jsonEnd + 1)]);
                score.Score = obj["risk_score"]?.Value<double>() ?? obj["score"]?.Value<double>() ?? 50;

                var levelStr = obj["risk_level"]?.ToString() ?? "";
                score.Level  = ParseRiskLevel(levelStr, score.Score);

                var factors = obj["risk_factors"] as JArray;
                if (factors != null)
                    score.RiskFactors = factors.Select(f => f.ToString()).ToList();
            }
            else
            {
                ParseRiskFromText(finalAnswer, score);
            }
        }
        catch
        {
            ParseRiskFromText(finalAnswer, score);
        }

        return score;
    }

    private static void ParseRiskFromText(string text, RiskScore score)
    {
        var scoreMatch = System.Text.RegularExpressions.Regex.Match(text,
            @"(?:risk score|score)[:\s]+(\d+(?:\.\d+)?)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        score.Score = scoreMatch.Success && double.TryParse(scoreMatch.Groups[1].Value, out var s) ? s : 50;

        var lower = text.ToLowerInvariant();
        if (lower.Contains("very high") || lower.Contains("veryhigh")) score.Level = RiskLevel.VeryHigh;
        else if (lower.Contains("high risk") || lower.Contains("high:")) score.Level = RiskLevel.High;
        else if (lower.Contains("medium") || lower.Contains("moderate")) score.Level = RiskLevel.Medium;
        else if (lower.Contains("low risk") || lower.Contains("low:")) score.Level = RiskLevel.Low;
        else score.Level = ParseRiskLevel("", score.Score);

        // Extract bullet points as risk factors
        score.RiskFactors = text.Split('\n')
            .Select(l => l.Trim().TrimStart('-', '•', '*', ' '))
            .Where(l => l.Length > 15 && l.Length < 200)
            .Take(5)
            .ToList();
    }

    private static RiskLevel ParseRiskLevel(string s, double numericScore) =>
        s.ToLower() switch
        {
            var v when v.Contains("very") || v.Contains("veryhigh") => RiskLevel.VeryHigh,
            var v when v.Contains("high")   => RiskLevel.High,
            var v when v.Contains("medium") || v.Contains("moderate") => RiskLevel.Medium,
            var v when v.Contains("low")    => RiskLevel.Low,
            _ => numericScore switch { >= 70 => RiskLevel.VeryHigh, >= 55 => RiskLevel.High, >= 35 => RiskLevel.Medium, _ => RiskLevel.Low }
        };
}
