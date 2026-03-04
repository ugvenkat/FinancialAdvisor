using FinancialAdvisor.Models;
using FinancialAdvisor.Services;
using FinancialAdvisor.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FinancialAdvisor.Agents;

/// <summary>
/// Agent 1: Data Collection Agent — TRUE AGENTIC
///
/// The LLM autonomously decides:
/// - Which data sources to query and in what order
/// - Whether it has enough data or needs more
/// - How to handle missing/partial data
///
/// It uses the ReAct loop to iteratively call tools until it has
/// a complete picture of the stock's data.
/// </summary>
public class DataCollectionAgent
{
    private readonly ReActEngine       _engine;
    private readonly FinancialToolkit  _toolkit;
    private readonly ILogger<DataCollectionAgent> _log;

    public DataCollectionAgent(ReActEngine engine, FinancialToolkit toolkit,
        ILogger<DataCollectionAgent> log)
    {
        _engine  = engine;
        _toolkit = toolkit;
        _log     = log;
    }

    public async Task<(StockRawData Data, AgentTrace Trace)> CollectAsync(
        string jobId, string ticker, CancellationToken ct = default)
    {
        _log.LogInformation("[DataCollectionAgent][{Ticker}] Starting agentic data collection", ticker);

        // Only expose the data-gathering tools to this agent
        var tools = FinancialToolkit.AllTools
            .Where(t => new[] { "get_stock_price", "get_financial_ratios", "get_latest_news",
                                 "get_analyst_ratings", "get_earnings_data", "get_marketwatch_news" }
                        .Contains(t.Name))
            .ToList();

        var executors = _toolkit.GetExecutors();

        var goal = $"""
            Collect comprehensive financial data for {ticker}.
            
            You MUST gather ALL of the following:
            1. Current stock price, P/E ratio, market cap, beta, 52-week range
            2. Financial ratios: P/B, debt-to-equity, free cash flow, revenue growth, EPS growth
            3. Latest news headlines (at least 8-10 articles)
            4. Analyst ratings and price targets
            5. Earnings data: EPS, revenue
            6. Additional news from MarketWatch
            
            Do not stop until you have data from ALL sources.
            In your FINAL ANSWER, provide a complete JSON summary of all collected data.
            """;

        var trace = await _engine.RunAsync(
            jobId, "DataCollectionAgent", ticker, goal, tools, executors, ct);

        // Parse the final answer back into our domain model
        var rawData = ParseCollectedData(ticker, trace.FinalAnswer);

        return (rawData, trace);
    }

    private StockRawData ParseCollectedData(string ticker, string finalAnswer)
    {
        var data = new StockRawData
        {
            Ticker      = ticker.ToUpper(),
            CompanyName = ResolveCompanyName(ticker),
            CollectedAt = DateTime.UtcNow
        };

        if (string.IsNullOrWhiteSpace(finalAnswer))
            return data;

        // Strip markdown code fences the LLM sometimes wraps JSON in
        finalAnswer = finalAnswer.Trim();
        if (finalAnswer.Contains("```"))
        {
            var fenceStart = finalAnswer.IndexOf('\n', finalAnswer.IndexOf("```"));
            var fenceEnd   = finalAnswer.LastIndexOf("```");
            if (fenceStart >= 0 && fenceEnd > fenceStart)
                finalAnswer = finalAnswer[(fenceStart + 1)..fenceEnd].Trim();
        }

        try
        {
            // Try to extract JSON from the final answer
            var jsonStart = finalAnswer.IndexOf('{');
            var jsonEnd   = finalAnswer.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonPart = finalAnswer[jsonStart..(jsonEnd + 1)];
                var obj      = JObject.Parse(jsonPart);

                // Parse metrics
                data.Metrics.CurrentPrice     = ParseDec(obj, "current_price", "price");
                data.Metrics.PERatio          = ParseDec(obj, "pe_ratio", "pe");
                data.Metrics.MarketCap        = ParseSuffixed(obj["market_cap"]?.ToString());
                data.Metrics.Beta             = ParseDec(obj, "beta");
                data.Metrics.EPS              = ParseDec(obj, "eps");
                data.Metrics.PBRatio          = ParseDec(obj, "pb_ratio");
                data.Metrics.DebtToEquity     = ParseDec(obj, "debt_to_equity");
                data.Metrics.FreeCashFlow     = ParseSuffixed(obj["free_cash_flow"]?.ToString());
                data.Metrics.FiftyTwoWeekHigh = ParseDec(obj, "52w_high", "week52_high");
                data.Metrics.FiftyTwoWeekLow  = ParseDec(obj, "52w_low",  "week52_low");
                data.Metrics.RevenueGrowthYoY = ParsePercent(obj["revenue_growth"]?.ToString());
                data.Metrics.EPSGrowthYoY     = ParsePercent(obj["eps_growth"]?.ToString());
                data.Metrics.Sector           = obj["sector"]?.ToString() ?? "";

                // Parse news
                var newsArr = obj["news"] as JArray;
                if (newsArr != null)
                    foreach (var n in newsArr)
                        data.News.Add(new NewsArticle
                        {
                            Title   = n["title"]?.ToString() ?? n.ToString(),
                            Summary = n["summary"]?.ToString() ?? "",
                            Source  = n["source"]?.ToString() ?? "Yahoo Finance"
                        });

                // Parse analyst ratings
                var ratArr = obj["analyst_ratings"] as JArray;
                if (ratArr != null)
                    foreach (var r in ratArr)
                        data.AnalystRatings.Add(new AnalystRating
                        {
                            Firm   = r["firm"]?.ToString() ?? "",
                            Rating = r["rating"]?.ToString() ?? "",
                            PriceTarget = decimal.TryParse(r["price_target"]?.ToString()?.Replace("$", ""), out var pt) ? pt : null
                        });
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not parse structured data from DataCollectionAgent final answer — using text");
        }

        // If we got no news from parsing, extract headlines from the text
        if (!data.News.Any())
        {
            var lines = finalAnswer.Split('\n')
                .Where(l => l.TrimStart().StartsWith("-") || l.TrimStart().StartsWith("•") || l.TrimStart().StartsWith("*"))
                .Select(l => l.Trim().TrimStart('-', '•', '*', ' '))
                .Where(l => l.Length > 20)
                .Take(10);
            foreach (var line in lines)
                data.News.Add(new NewsArticle { Title = line, Source = "Extracted" });
        }

        return data;
    }

    private static decimal? ParseDec(JObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            var val = obj[key]?.ToString();
            if (!string.IsNullOrEmpty(val) && val != "N/A")
                if (decimal.TryParse(val.Replace(",", "").Replace("$", ""),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                    return d;
        }
        return null;
    }

    private static decimal? ParseSuffixed(string? s)
    {
        if (string.IsNullOrEmpty(s) || s == "N/A") return null;
        s = s.Replace("$", "").Trim();
        var mult = 1m;
        if (s.EndsWith("T", StringComparison.OrdinalIgnoreCase)) { mult = 1_000_000_000_000m; s = s[..^1]; }
        else if (s.EndsWith("B", StringComparison.OrdinalIgnoreCase)) { mult = 1_000_000_000m; s = s[..^1]; }
        else if (s.EndsWith("M", StringComparison.OrdinalIgnoreCase)) { mult = 1_000_000m; s = s[..^1]; }
        return decimal.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v * mult : null;
    }

    private static decimal? ParsePercent(string? s)
    {
        if (string.IsNullOrEmpty(s) || s == "N/A") return null;
        s = s.Replace("%", "").Trim();
        if (decimal.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v))
            return Math.Abs(v) > 1 ? v / 100m : v; // handle both 0.15 and 15%
        return null;
    }

    private static string ResolveCompanyName(string ticker) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AAPL"] = "Apple Inc.",        ["MSFT"] = "Microsoft Corporation",
            ["GOOGL"]= "Alphabet Inc.",     ["AMZN"] = "Amazon.com Inc.",
            ["NVDA"] = "NVIDIA Corporation",["META"] = "Meta Platforms Inc.",
            ["TSLA"] = "Tesla Inc.",        ["JPM"]  = "JPMorgan Chase & Co.",
            ["V"]    = "Visa Inc.",         ["JNJ"]  = "Johnson & Johnson",
            ["WMT"]  = "Walmart Inc.",      ["XOM"]  = "ExxonMobil Corporation",
            ["NFLX"] = "Netflix Inc.",      ["INTC"] = "Intel Corporation",
            ["DIS"]  = "Walt Disney Company"
        }.TryGetValue(ticker.ToUpper(), out var name) ? name : $"{ticker.ToUpper()} Corp.";
}
