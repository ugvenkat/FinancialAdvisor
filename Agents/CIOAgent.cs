using FinancialAdvisor.Models;
using FinancialAdvisor.Tools;
using Newtonsoft.Json.Linq;

namespace FinancialAdvisor.Agents;

/// <summary>
/// Agent 5: Chief Investment Officer — TRUE AGENTIC
///
/// The most sophisticated agent. The LLM:
/// - Reviews ALL prior agent outputs
/// - Can call compute_price_target to calculate targets
/// - Weighs conflicting signals (e.g. strong fundamentals but high risk)
/// - Reasons through its investment thesis step-by-step
/// - Makes the final Buy/Hold/Sell call with confidence
///
/// This is genuine AI reasoning — not rule-based decision trees.
/// </summary>
public class ChiefInvestmentOfficerAgent
{
    private readonly ReActEngine      _engine;
    private readonly FinancialToolkit _toolkit;
    private readonly ILogger<ChiefInvestmentOfficerAgent> _log;

    public ChiefInvestmentOfficerAgent(ReActEngine engine, FinancialToolkit toolkit,
        ILogger<ChiefInvestmentOfficerAgent> log)
    {
        _engine  = engine;
        _toolkit = toolkit;
        _log     = log;
    }

    public async Task<(InvestmentRecommendation Rec, AgentTrace Trace)> DecideAsync(
        string jobId,
        StockRawData rawData,
        FundamentalScore fundamental,
        SentimentScore sentiment,
        RiskScore risk,
        CancellationToken ct = default)
    {
        _log.LogInformation("[CIOAgent][{Ticker}] Starting agentic investment decision", rawData.Ticker);

        // CIO only needs the price target tool — it reasons from the other agents' outputs
        var tools = FinancialToolkit.AllTools
            .Where(t => t.Name == "compute_price_target")
            .ToList();

        var executors = _toolkit.GetExecutors();

        // Analyst consensus summary
        var analystSummary = rawData.AnalystRatings.Any()
            ? $"{rawData.AnalystRatings.Count(r => r.Rating.ToLower().Contains("buy") || r.Rating.ToLower().Contains("overweight"))} Buy, " +
              $"{rawData.AnalystRatings.Count(r => r.Rating.ToLower().Contains("hold") || r.Rating.ToLower().Contains("neutral"))} Hold, " +
              $"{rawData.AnalystRatings.Count(r => r.Rating.ToLower().Contains("sell") || r.Rating.ToLower().Contains("underperform"))} Sell"
            : "No analyst ratings available";

        var goal = $"""
            You are the Chief Investment Officer. Make a final investment decision for {rawData.Ticker} ({rawData.CompanyName}).

            ═══ AGENT REPORTS ═══

            📊 FUNDAMENTAL ANALYSIS AGENT REPORT:
            Score: {fundamental.Score:F1}/100 | Grade: {fundamental.Grade}
            Strengths: {string.Join("; ", fundamental.Strengths.Take(3))}
            Weaknesses: {string.Join("; ", fundamental.Weaknesses.Take(3))}
            Analysis: {fundamental.DetailedAnalysis[..Math.Min(300, fundamental.DetailedAnalysis.Length)]}

            📰 SENTIMENT ANALYSIS AGENT REPORT:
            Overall: {sentiment.Overall} | Score: {sentiment.Score:+0.00;-0.00}
            Breakdown: {sentiment.BullishPercent:F0}% Bullish / {sentiment.NeutralPercent:F0}% Neutral / {sentiment.BearishPercent:F0}% Bearish
            Summary: {sentiment.SentimentSummary[..Math.Min(300, sentiment.SentimentSummary.Length)]}

            ⚠️ RISK ANALYSIS AGENT REPORT:
            Level: {risk.Level} | Score: {risk.Score:F1}/100
            Key Risks: {string.Join("; ", risk.RiskFactors.Take(3))}
            Summary: {risk.RiskSummary[..Math.Min(300, risk.RiskSummary.Length)]}

            👔 ANALYST CONSENSUS: {analystSummary}

            📈 MARKET DATA:
            Current Price: ${rawData.Metrics.CurrentPrice:F2}
            P/E: {rawData.Metrics.PERatio:F1} | EPS: ${rawData.Metrics.EPS:F2} | EPS Growth: {rawData.Metrics.EPSGrowthYoY:P1}
            52W Range: ${rawData.Metrics.FiftyTwoWeekLow:F2} – ${rawData.Metrics.FiftyTwoWeekHigh:F2}
            Sector: {rawData.Metrics.Sector}

            ═══ YOUR DECISION PROCESS ═══

            Step 1: Weigh the signals — do they agree or conflict?
            Step 2: Consider the risk-adjusted opportunity
            Step 3: Determine your action: StrongBuy / Buy / Hold / Sell / StrongSell
            Step 4: Call compute_price_target to calculate a price target
            Step 5: State your confidence (0-100%) and time horizon
            Step 6: Identify the top catalysts and risks

            In your FINAL ANSWER provide a complete investment recommendation with:
            - Action (StrongBuy/Buy/Hold/Sell/StrongSell)
            - Confidence percentage
            - Price target and upside/downside %
            - Time horizon
            - Top 3 catalysts
            - Top 3 risks
            - Your CIO investment memo (3-4 sentences of professional analysis)
            """;

        var trace = await _engine.RunAsync(
            jobId, "CIOAgent", rawData.Ticker, goal, tools, executors, ct);

        var rec = ParseRecommendation(rawData, fundamental, sentiment, risk, trace.FinalAnswer);
        rec.CIOTrace = trace;

        _log.LogInformation("[CIOAgent][{Ticker}] Decision: {Action} ({Confidence:F0}%) | Target: ${Target:F2} | in {Steps} steps",
            rawData.Ticker, rec.Action, rec.Confidence, rec.PriceTarget, trace.TotalSteps);

        return (rec, trace);
    }

    private static InvestmentRecommendation ParseRecommendation(
        StockRawData raw, FundamentalScore fund, SentimentScore sent, RiskScore risk,
        string finalAnswer)
    {
        var rec = new InvestmentRecommendation
        {
            Ticker       = raw.Ticker,
            CompanyName  = raw.CompanyName,
            CurrentPrice = raw.Metrics.CurrentPrice,
            Fundamental  = fund,
            Sentiment    = sent,
            Risk         = risk,
            CIOSummary   = finalAnswer
        };

        try
        {
            // Try JSON extraction
            var jsonStart = finalAnswer.IndexOf('{');
            var jsonEnd   = finalAnswer.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var obj = JObject.Parse(finalAnswer[jsonStart..(jsonEnd + 1)]);

                if (Enum.TryParse<Models.Action>(obj["action"]?.ToString() ?? "", true, out var act))
                    rec.Action = act;

                rec.Confidence   = obj["confidence"]?.Value<double>() ?? 60;
                rec.PriceTarget  = obj["price_target"]?.Value<decimal?>() ?? obj["target"]?.Value<decimal?>();
                rec.TimeHorizon  = obj["time_horizon"]?.ToString() ?? "6-12 months";

                var cats = obj["catalysts"] as JArray ?? obj["key_catalysts"] as JArray;
                if (cats != null) rec.KeyCatalysts = cats.Select(c => c.ToString()).ToList();

                var risks = obj["risks"] as JArray ?? obj["key_risks"] as JArray;
                if (risks != null) rec.KeyRisks = risks.Select(r => r.ToString()).ToList();

                rec.Rationale = obj["rationale"]?.ToString() ?? obj["memo"]?.ToString() ?? "";
            }
            else
            {
                ParseRecommendationFromText(finalAnswer, rec);
            }
        }
        catch
        {
            ParseRecommendationFromText(finalAnswer, rec);
        }

        // Compute upside if we have prices
        if (rec.CurrentPrice.HasValue && rec.PriceTarget.HasValue && rec.CurrentPrice > 0)
            rec.UpsidePercent = (double)((rec.PriceTarget.Value - rec.CurrentPrice.Value) / rec.CurrentPrice.Value * 100);

        // Sanity defaults
        if (rec.Confidence < 10) rec.Confidence = DeriveConfidence(fund, sent, risk);
        rec.RiskLevel = risk.Level;

        return rec;
    }

    private static void ParseRecommendationFromText(string text, InvestmentRecommendation rec)
    {
        // Action extraction
        foreach (var action in new[] { "StrongBuy", "StrongSell", "Buy", "Sell", "Hold" })
            if (text.Contains(action, StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.TryParse<Models.Action>(action, true, out var a)) { rec.Action = a; break; }
            }

        // Confidence
        var confMatch = System.Text.RegularExpressions.Regex.Match(text,
            @"confidence[:\s]+(\d+)%?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        rec.Confidence = confMatch.Success && double.TryParse(confMatch.Groups[1].Value, out var c) ? c : 60;

        // Price target
        var ptMatch = System.Text.RegularExpressions.Regex.Match(text,
            @"(?:price target|target)[:\s]+\$(\d+(?:\.\d+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (ptMatch.Success && decimal.TryParse(ptMatch.Groups[1].Value, out var pt)) rec.PriceTarget = pt;

        // Time horizon
        var thMatch = System.Text.RegularExpressions.Regex.Match(text,
            @"(\d+[-–]\d+\s+(?:months?|years?))", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        rec.TimeHorizon = thMatch.Success ? thMatch.Value : "6-12 months";

        // Extract bullets for catalysts/risks
        var lines = text.Split('\n').Select(l => l.Trim().TrimStart('-', '•', '*', ' '))
            .Where(l => l.Length > 15).ToList();
        var inCatalysts = false; var inRisks = false;
        foreach (var line in lines)
        {
            if (line.ToLower().Contains("catalyst")) { inCatalysts = true; inRisks = false; continue; }
            if (line.ToLower().Contains("risk"))     { inRisks = true; inCatalysts = false; continue; }
            if (inCatalysts && rec.KeyCatalysts.Count < 4) rec.KeyCatalysts.Add(line);
            if (inRisks     && rec.KeyRisks.Count < 4)     rec.KeyRisks.Add(line);
        }
    }

    private static double DeriveConfidence(FundamentalScore fund, SentimentScore sent, RiskScore risk)
    {
        var signals = new[] { fund.Score, (sent.Score + 1) / 2.0 * 100, 100 - risk.Score };
        var avg = signals.Average();
        var stdDev = Math.Sqrt(signals.Sum(s => Math.Pow(s - avg, 2)) / signals.Length);
        return Math.Max(40, Math.Min(90, 90 - stdDev * 0.4));
    }
}
