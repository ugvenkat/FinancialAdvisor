using HtmlAgilityPack;
using FinancialAdvisor.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FinancialAdvisor.Tools;

/// <summary>
/// All tools available to agents.
/// Uses Yahoo Finance v8 JSON API (reliable, no HTML scraping for price data).
/// Falls back to HTML scraping for news and ratings.
/// </summary>
public class FinancialToolkit
{
    private readonly HttpClient _http;
    private readonly ILogger<FinancialToolkit> _log;

    public FinancialToolkit(HttpClient http, ILogger<FinancialToolkit> log)
    {
        _http = http;
        _log  = log;
    }

    // ── Tool Definitions (shown to LLM) ──────────────────────────────────

    public static List<AgentTool> AllTools => new()
    {
        new AgentTool
        {
            Name        = "get_stock_price",
            Description = "Fetches current stock price, 52-week range, market cap, P/E ratio, beta, EPS, and dividend yield using Yahoo Finance JSON API.",
            Parameters  = """{"ticker": "string — stock symbol e.g. AAPL"}"""
        },
        new AgentTool
        {
            Name        = "get_financial_ratios",
            Description = "Fetches detailed financial ratios: P/B ratio, debt-to-equity, free cash flow, revenue growth, EPS growth from Yahoo Finance.",
            Parameters  = """{"ticker": "string — stock symbol"}"""
        },
        new AgentTool
        {
            Name        = "get_latest_news",
            Description = "Fetches the latest news headlines for a stock from Yahoo Finance RSS feed.",
            Parameters  = """{"ticker": "string — stock symbol"}"""
        },
        new AgentTool
        {
            Name        = "get_analyst_ratings",
            Description = "Fetches analyst consensus: number of Strong Buy, Buy, Hold, Sell, Strong Sell ratings.",
            Parameters  = """{"ticker": "string — stock symbol"}"""
        },
        new AgentTool
        {
            Name        = "get_earnings_data",
            Description = "Fetches earnings data: EPS estimate vs actual, revenue, surprise percentage.",
            Parameters  = """{"ticker": "string — stock symbol"}"""
        },
        new AgentTool
        {
            Name        = "get_marketwatch_news",
            Description = "Fetches additional news headlines from MarketWatch.",
            Parameters  = """{"ticker": "string — stock symbol"}"""
        },
        new AgentTool
        {
            Name        = "classify_sentiment",
            Description = "Classifies a list of news headlines as Bullish, Neutral, or Bearish using keyword analysis.",
            Parameters  = """{"headlines": "array of strings — news headlines to classify"}"""
        },
        new AgentTool
        {
            Name        = "calculate_fundamental_score",
            Description = "Calculates a fundamental score (0-100) and grade (A-F) from financial metrics.",
            Parameters  = """{"pe_ratio": "number|null", "eps_growth": "number|null", "revenue_growth": "number|null", "debt_to_equity": "number|null", "free_cash_flow_billions": "number|null"}"""
        },
        new AgentTool
        {
            Name        = "calculate_risk_score",
            Description = "Calculates a risk score (0-100) and risk level from volatility, valuation, and debt metrics.",
            Parameters  = """{"beta": "number|null", "pe_ratio": "number|null", "debt_to_equity": "number|null", "sector": "string", "current_price": "number|null", "week52_high": "number|null", "week52_low": "number|null"}"""
        },
        new AgentTool
        {
            Name        = "compute_price_target",
            Description = "Computes a price target using earnings-based and multiple-based methods.",
            Parameters  = """{"current_price": "number", "eps": "number|null", "eps_growth": "number|null", "pe_ratio": "number|null", "action": "StrongBuy|Buy|Hold|Sell|StrongSell"}"""
        }
    };

    // ── Tool Executor Map ─────────────────────────────────────────────────

    public Dictionary<string, Func<string, Task<string>>> GetExecutors() => new()
    {
        ["get_stock_price"]             = GetStockPriceAsync,
        ["get_financial_ratios"]        = GetFinancialRatiosAsync,
        ["get_latest_news"]             = GetLatestNewsAsync,
        ["get_analyst_ratings"]         = GetAnalystRatingsAsync,
        ["get_earnings_data"]           = GetEarningsDataAsync,
        ["get_marketwatch_news"]        = GetMarketWatchNewsAsync,
        ["classify_sentiment"]          = ClassifySentimentAsync,
        ["calculate_fundamental_score"] = CalculateFundamentalScoreAsync,
        ["calculate_risk_score"]        = CalculateRiskScoreAsync,
        ["compute_price_target"]        = ComputePriceTargetAsync
    };

    // ══════════════════════════════════════════════════════════════════════
    //  TOOL IMPLEMENTATIONS
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Uses Yahoo Finance v8 quote API — returns clean JSON, no HTML parsing needed.
    /// URL: https://query1.finance.yahoo.com/v8/finance/chart/{ticker}
    /// </summary>
    private async Task<string> GetStockPriceAsync(string input)
    {
        var ticker = ExtractTicker(input);
        try
        {
            // v8 chart API — reliable JSON endpoint
            var url  = $"https://query1.finance.yahoo.com/v8/finance/chart/{ticker}?interval=1d&range=1d";
            var json = await FetchJsonAsync(url);
            if (json == null)
            {
                // fallback to v7 quote summary
                url  = $"https://query1.finance.yahoo.com/v7/finance/quote?symbols={ticker}";
                json = await FetchJsonAsync(url);
            }
            if (json == null) return $"Could not fetch price data for {ticker} — Yahoo Finance API unavailable";

            var result = new Dictionary<string, object?>();

            // Try v8 chart response shape
            var chart = json["chart"]?["result"]?[0];
            if (chart != null)
            {
                var meta = chart["meta"];
                result["ticker"]         = ticker;
                result["current_price"]  = meta?["regularMarketPrice"]?.Value<double?>();
                result["previous_close"] = meta?["previousClose"]?.Value<double?>();
                result["market_cap"]     = meta?["marketCap"]?.Value<double?>();
                result["52w_high"]       = meta?["fiftyTwoWeekHigh"]?.Value<double?>();
                result["52w_low"]        = meta?["fiftyTwoWeekLow"]?.Value<double?>();
                result["currency"]       = meta?["currency"]?.ToString();
                result["exchange"]       = meta?["exchangeName"]?.ToString();
            }

            // Try v7 quote response shape
            var quotes = json["quoteResponse"]?["result"]?[0];
            if (quotes != null)
            {
                result["ticker"]                = ticker;
                result["current_price"]         = quotes["regularMarketPrice"]?.Value<double?>();
                result["previous_close"]        = quotes["regularMarketPreviousClose"]?.Value<double?>();
                result["market_cap"]            = quotes["marketCap"]?.Value<double?>();
                result["pe_ratio"]              = quotes["trailingPE"]?.Value<double?>();
                result["eps"]                   = quotes["epsTrailingTwelveMonths"]?.Value<double?>();
                result["beta"]                  = quotes["beta"]?.Value<double?>();
                result["52w_high"]              = quotes["fiftyTwoWeekHigh"]?.Value<double?>();
                result["52w_low"]               = quotes["fiftyTwoWeekLow"]?.Value<double?>();
                result["dividend_yield"]        = quotes["dividendYield"]?.Value<double?>();
                result["forward_pe"]            = quotes["forwardPE"]?.Value<double?>();
                result["price_to_book"]         = quotes["priceToBook"]?.Value<double?>();
                result["sector"]                = quotes["sector"]?.ToString();
                result["industry"]              = quotes["industry"]?.ToString();
                result["full_name"]             = quotes["longName"]?.ToString();
                result["50day_avg"]             = quotes["fiftyDayAverage"]?.Value<double?>();
                result["200day_avg"]            = quotes["twoHundredDayAverage"]?.Value<double?>();
                result["avg_volume"]            = quotes["averageDailyVolume3Month"]?.Value<long?>();
            }

            var cleaned = result.Where(kv => kv.Value != null).ToDictionary(k => k.Key, v => v.Value);
            return cleaned.Any()
                ? JsonConvert.SerializeObject(cleaned, Formatting.Indented)
                : $"No data returned for {ticker}";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "get_stock_price failed for {T}", ticker);
            return $"Error fetching stock price for {ticker}: {ex.Message}";
        }
    }

    /// <summary>
    /// Uses Yahoo Finance v11 quoteSummary API for detailed financial stats.
    /// </summary>
    private async Task<string> GetFinancialRatiosAsync(string input)
    {
        var ticker = ExtractTicker(input);
        try
        {
            var url  = $"https://query1.finance.yahoo.com/v11/finance/quoteSummary/{ticker}" +
                       $"?modules=financialData,defaultKeyStatistics,incomeStatementHistory";
            var json = await FetchJsonAsync(url);
            if (json == null) return $"Could not fetch financial ratios for {ticker}";

            var result  = new Dictionary<string, object?>();
            var summary = json["quoteSummary"]?["result"]?[0];
            if (summary == null) return $"No data in quoteSummary for {ticker}";

            var fin  = summary["financialData"];
            var stat = summary["defaultKeyStatistics"];

            if (fin != null)
            {
                result["current_ratio"]     = fin["currentRatio"]?["raw"]?.Value<double?>();
                result["debt_to_equity"]    = fin["debtToEquity"]?["raw"]?.Value<double?>();
                result["free_cash_flow"]    = fin["freeCashflow"]?["raw"]?.Value<double?>();
                result["revenue_growth"]    = fin["revenueGrowth"]?["raw"]?.Value<double?>();
                result["earnings_growth"]   = fin["earningsGrowth"]?["raw"]?.Value<double?>();
                result["gross_margins"]     = fin["grossMargins"]?["raw"]?.Value<double?>();
                result["profit_margins"]    = fin["profitMargins"]?["raw"]?.Value<double?>();
                result["return_on_equity"]  = fin["returnOnEquity"]?["raw"]?.Value<double?>();
                result["return_on_assets"]  = fin["returnOnAssets"]?["raw"]?.Value<double?>();
                result["total_revenue"]     = fin["totalRevenue"]?["raw"]?.Value<double?>();
                result["total_cash"]        = fin["totalCash"]?["raw"]?.Value<double?>();
                result["total_debt"]        = fin["totalDebt"]?["raw"]?.Value<double?>();
                result["operating_cashflow"]= fin["operatingCashflow"]?["raw"]?.Value<double?>();
                result["target_mean_price"] = fin["targetMeanPrice"]?["raw"]?.Value<double?>();
                result["analyst_recommendation"] = fin["recommendationKey"]?.ToString();
            }

            if (stat != null)
            {
                result["enterprise_value"]  = stat["enterpriseValue"]?["raw"]?.Value<double?>();
                result["forward_pe"]        = stat["forwardPE"]?["raw"]?.Value<double?>();
                result["pb_ratio"]          = stat["priceToBook"]?["raw"]?.Value<double?>();
                result["peg_ratio"]         = stat["pegRatio"]?["raw"]?.Value<double?>();
                result["short_ratio"]       = stat["shortRatio"]?["raw"]?.Value<double?>();
                result["beta"]              = stat["beta"]?["raw"]?.Value<double?>();
                result["shares_outstanding"]= stat["sharesOutstanding"]?["raw"]?.Value<double?>();
                result["book_value"]        = stat["bookValue"]?["raw"]?.Value<double?>();
                result["eps_trailing"]      = stat["trailingEps"]?["raw"]?.Value<double?>();
                result["eps_forward"]       = stat["forwardEps"]?["raw"]?.Value<double?>();
            }

            var cleaned = result.Where(kv => kv.Value != null).ToDictionary(k => k.Key, v => v.Value);
            return cleaned.Any()
                ? JsonConvert.SerializeObject(cleaned, Formatting.Indented)
                : $"No ratio data for {ticker}";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "get_financial_ratios failed for {T}", ticker);
            return $"Error fetching ratios for {ticker}: {ex.Message}";
        }
    }

    /// <summary>Yahoo Finance RSS — very reliable, plain XML.</summary>
    private async Task<string> GetLatestNewsAsync(string input)
    {
        var ticker = ExtractTicker(input);
        try
        {
            var rss = await FetchRawAsync(
                $"https://feeds.finance.yahoo.com/rss/2.0/headline?s={ticker}&region=US&lang=en-US");
            if (rss == null) return $"No news found for {ticker}";

            var doc = new HtmlDocument();
            doc.LoadHtml(rss);

            var items = doc.DocumentNode.SelectNodes("//item");
            if (items == null) return $"No news items for {ticker}";

            var articles = items.Take(10).Select(item =>
            {
                var title   = HtmlEntity.DeEntitize(item.SelectSingleNode("title")?.InnerText.Trim() ?? "");
                var desc    = HtmlEntity.DeEntitize(item.SelectSingleNode("description")?.InnerText.Trim() ?? "");
                var pubDate = item.SelectSingleNode("pubdate")?.InnerText.Trim() ?? "";
                return new { title, summary = desc.Length > 200 ? desc[..200] : desc, source = "Yahoo Finance", date = pubDate };
            })
            .Where(a => !string.IsNullOrEmpty(a.title))
            .ToList();

            return articles.Any()
                ? JsonConvert.SerializeObject(articles, Formatting.Indented)
                : $"No news articles found for {ticker}";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "get_latest_news failed for {T}", ticker);
            return $"Error fetching news for {ticker}: {ex.Message}";
        }
    }

    /// <summary>Uses Yahoo Finance quoteSummary recommendationTrend module.</summary>
    private async Task<string> GetAnalystRatingsAsync(string input)
    {
        var ticker = ExtractTicker(input);
        try
        {
            var url  = $"https://query1.finance.yahoo.com/v11/finance/quoteSummary/{ticker}" +
                       $"?modules=recommendationTrend,upgradeDowngradeHistory";
            var json = await FetchJsonAsync(url);
            if (json == null) return $"No analyst data for {ticker}";

            var summary = json["quoteSummary"]?["result"]?[0];
            if (summary == null) return $"No analyst summary for {ticker}";

            var result = new Dictionary<string, object?>();

            // Consensus trend (most recent period)
            var trend = summary["recommendationTrend"]?["trend"]?[0];
            if (trend != null)
            {
                result["strong_buy"]  = trend["strongBuy"]?.Value<int?>();
                result["buy"]         = trend["buy"]?.Value<int?>();
                result["hold"]        = trend["hold"]?.Value<int?>();
                result["sell"]        = trend["sell"]?.Value<int?>();
                result["strong_sell"] = trend["strongSell"]?.Value<int?>();
                result["period"]      = trend["period"]?.ToString();
            }

            // Recent upgrades/downgrades
            var history = summary["upgradeDowngradeHistory"]?["history"];
            if (history != null)
            {
                var recent = history.Take(5).Select(h => new
                {
                    firm   = h["firm"]?.ToString(),
                    action = h["action"]?.ToString(),
                    from   = h["fromGrade"]?.ToString(),
                    to     = h["toGrade"]?.ToString(),
                    date   = h["epochGradeDate"] != null
                        ? DateTimeOffset.FromUnixTimeSeconds(h["epochGradeDate"]!.Value<long>()).ToString("yyyy-MM-dd")
                        : ""
                }).ToList();
                result["recent_changes"] = recent;
            }

            return JsonConvert.SerializeObject(result, Formatting.Indented);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "get_analyst_ratings failed for {T}", ticker);
            return $"Error fetching analyst ratings for {ticker}: {ex.Message}";
        }
    }

    /// <summary>Uses Yahoo Finance quoteSummary earnings module.</summary>
    private async Task<string> GetEarningsDataAsync(string input)
    {
        var ticker = ExtractTicker(input);
        try
        {
            var url  = $"https://query1.finance.yahoo.com/v11/finance/quoteSummary/{ticker}" +
                       $"?modules=earnings,earningsTrend,earningsHistory";
            var json = await FetchJsonAsync(url);
            if (json == null) return $"No earnings data for {ticker}";

            var summary = json["quoteSummary"]?["result"]?[0];
            if (summary == null) return $"No earnings summary for {ticker}";

            var result = new Dictionary<string, object?>();

            // Earnings history
            var history = summary["earningsHistory"]?["history"];
            if (history != null)
            {
                var quarters = history.Take(4).Select(q => new
                {
                    quarter          = q["quarter"]?["fmt"]?.ToString(),
                    eps_actual       = q["epsActual"]?["raw"]?.Value<double?>(),
                    eps_estimate     = q["epsEstimate"]?["raw"]?.Value<double?>(),
                    surprise_percent = q["surprisePercent"]?["raw"]?.Value<double?>()
                }).ToList();
                result["quarterly_earnings"] = quarters;
            }

            // Earnings trend (forward estimates)
            var trend = summary["earningsTrend"]?["trend"];
            if (trend != null)
            {
                var current = trend.FirstOrDefault(t => t["period"]?.ToString() == "0q");
                if (current != null)
                {
                    result["current_quarter_estimate"] = current["earningsEstimate"]?["avg"]?["raw"]?.Value<double?>();
                    result["revenue_estimate"]         = current["revenueEstimate"]?["avg"]?["raw"]?.Value<double?>();
                    result["growth_estimate"]          = current["growth"]?["raw"]?.Value<double?>();
                }
            }

            return JsonConvert.SerializeObject(result, Formatting.Indented);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "get_earnings_data failed for {T}", ticker);
            return $"Error fetching earnings for {ticker}: {ex.Message}";
        }
    }

    private async Task<string> GetMarketWatchNewsAsync(string input)
    {
        var ticker = ExtractTicker(input);
        try
        {
            var html = await FetchRawAsync(
                $"https://www.marketwatch.com/investing/stock/{ticker.ToLower()}/newsviewer");
            if (html == null) return $"No MarketWatch news for {ticker}";

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var headlines = doc.DocumentNode
                .SelectNodes("//h3[contains(@class,'article__headline')]")
                ?.Take(8)
                .Select(h => HtmlEntity.DeEntitize(h.InnerText.Trim()))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            return headlines?.Any() == true
                ? JsonConvert.SerializeObject(new { source = "MarketWatch", headlines }, Formatting.Indented)
                : $"No MarketWatch headlines found for {ticker}";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "get_marketwatch_news failed for {T}", ticker);
            return $"Error fetching MarketWatch news for {ticker}: {ex.Message}";
        }
    }

    private Task<string> ClassifySentimentAsync(string input)
    {
        try
        {
            string[]? headlines = null;
            try
            {
                var obj = JObject.Parse(input);
                headlines = obj["headlines"]?.ToObject<string[]>();
            }
            catch { headlines = JsonConvert.DeserializeObject<string[]>(input); }

            if (headlines == null || !headlines.Any())
                return Task.FromResult("Error: no headlines provided");

            string[] bullishWords = ["beat", "record", "surge", "rally", "upgrade", "buy", "outperform",
                "growth", "profit", "exceeds", "strong", "positive", "soar", "gains", "bullish",
                "dividend", "buyback", "partnership", "breakthrough", "wins", "raises", "lifts", "jumps"];
            string[] bearishWords = ["miss", "loss", "decline", "downgrade", "sell", "underperform", "cut",
                "layoff", "warning", "investigation", "lawsuit", "debt", "default", "bearish",
                "recession", "fraud", "risk", "fail", "drop", "crash", "concern", "lowers", "slashes", "falls"];

            var results = headlines.Select(h =>
            {
                var text = h.ToLowerInvariant();
                var bull = bullishWords.Count(w => text.Contains(w));
                var bear = bearishWords.Count(w => text.Contains(w));
                var sent = bull > bear ? "Bullish" : bear > bull ? "Bearish" : "Neutral";
                var conf = Math.Abs(bull - bear) switch { >= 3 => 0.90, >= 2 => 0.75, >= 1 => 0.60, _ => 0.45 };
                return new { headline = h, sentiment = sent, confidence = conf };
            }).ToList();

            var bullCount = results.Count(r => r.sentiment == "Bullish");
            var bearCount = results.Count(r => r.sentiment == "Bearish");
            var neutCount = results.Count - bullCount - bearCount;
            var total     = results.Count;

            return Task.FromResult(JsonConvert.SerializeObject(new
            {
                classifications = results,
                summary = new
                {
                    total,
                    bullish_count  = bullCount,
                    neutral_count  = neutCount,
                    bearish_count  = bearCount,
                    bullish_pct    = total > 0 ? Math.Round(bullCount * 100.0 / total, 1) : 0,
                    bearish_pct    = total > 0 ? Math.Round(bearCount * 100.0 / total, 1) : 0,
                    overall_score  = total > 0 ? Math.Round((bullCount - bearCount) / (double)total, 2) : 0
                }
            }, Formatting.Indented));
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    private Task<string> CalculateFundamentalScoreAsync(string input)
    {
        try
        {
            var p = JObject.Parse(input);
            double total = 0, weight = 0;
            var breakdown  = new Dictionary<string, object>();
            var strengths  = new List<string>();
            var weaknesses = new List<string>();

            void Score(string name, double? val, Func<double, double> scorer, double w, string unit = "")
            {
                if (!val.HasValue) { total += w * 0.5; weight += w; breakdown[name] = "N/A (neutral 50pts)"; return; }
                var pts = scorer(val.Value);
                total += pts * w; weight += w;
                breakdown[name] = $"{val.Value:F2}{unit} → {pts * 100:F0}/100";
                if (pts > 0.75) strengths.Add($"{name}: {val.Value:F2}{unit}");
                if (pts < 0.35) weaknesses.Add($"{name}: {val.Value:F2}{unit}");
            }

            Score("pe_ratio",              p["pe_ratio"]?.Value<double?>(),
                v => v < 15 ? 1.0 : v < 25 ? 0.8 : v < 40 ? 0.5 : v < 60 ? 0.25 : 0.1, 1.0);
            Score("eps_growth",            p["eps_growth"]?.Value<double?>(),
                v => v > 0.25 ? 1.0 : v > 0.10 ? 0.75 : v > 0 ? 0.5 : 0.1, 1.0, "%");
            Score("revenue_growth",        p["revenue_growth"]?.Value<double?>(),
                v => v > 0.20 ? 1.0 : v > 0.10 ? 0.75 : v > 0.05 ? 0.5 : v > 0 ? 0.25 : 0.1, 1.0, "%");
            Score("debt_to_equity",        p["debt_to_equity"]?.Value<double?>(),
                v => v < 0.5 ? 1.0 : v < 1.0 ? 0.8 : v < 2.0 ? 0.5 : v < 3.0 ? 0.25 : 0.1, 1.0);
            Score("free_cash_flow",        p["free_cash_flow_billions"]?.Value<double?>(),
                v => v > 10 ? 1.0 : v > 1 ? 0.75 : v > 0 ? 0.5 : 0.1, 1.0, "B");

            var finalScore = weight > 0 ? (total / weight) * 100 : 50;
            var grade = finalScore switch { >= 85 => "A", >= 70 => "B", >= 55 => "C", >= 40 => "D", _ => "F" };

            return Task.FromResult(JsonConvert.SerializeObject(new
            {
                score = Math.Round(finalScore, 1), grade, breakdown, strengths, weaknesses
            }, Formatting.Indented));
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    private Task<string> CalculateRiskScoreAsync(string input)
    {
        try
        {
            var p      = JObject.Parse(input);
            var beta   = p["beta"]?.Value<double?>();
            var pe     = p["pe_ratio"]?.Value<double?>();
            var de     = p["debt_to_equity"]?.Value<double?>();
            var sector = p["sector"]?.ToString() ?? "";
            var cur    = p["current_price"]?.Value<double?>();
            var hi52   = p["week52_high"]?.Value<double?>();
            var lo52   = p["week52_low"]?.Value<double?>();
            var factors = new List<string>();
            double riskSum = 0; int cnt = 0;

            if (beta.HasValue)
            {
                var br = beta.Value switch { > 2.0 => 90, > 1.5 => 75, > 1.2 => 60, > 0.8 => 40, _ => 20 };
                riskSum += br; cnt++;
                if (beta.Value > 1.5) factors.Add($"High beta ({beta.Value:F2}) — elevated volatility");
            }
            else { riskSum += 50; cnt++; }

            if (pe.HasValue)
            {
                var vr = pe.Value switch { > 80 => 90, > 50 => 75, > 35 => 60, > 20 => 35, _ => 15 };
                riskSum += vr; cnt++;
                if (pe.Value > 60) factors.Add($"Very high P/E ({pe.Value:F1}) — priced for perfection");
            }
            else { riskSum += 50; cnt++; }

            if (de.HasValue)
            {
                var dr = de.Value switch { > 4 => 85, > 2.5 => 70, > 1.5 => 50, > 0.5 => 30, _ => 15 };
                riskSum += dr; cnt++;
                if (de.Value > 3) factors.Add($"High leverage (D/E {de.Value:F2})");
            }
            else { riskSum += 40; cnt++; }

            if (cur.HasValue && hi52.HasValue && lo52.HasValue)
            {
                var range = hi52.Value - lo52.Value;
                var pos   = range > 0 ? (cur.Value - lo52.Value) / range : 0.5;
                riskSum += pos > 0.9 ? 70 : pos > 0.7 ? 45 : pos > 0.4 ? 30 : 50; cnt++;
                if (pos > 0.9) factors.Add("Near 52-week high — limited upside margin");
            }

            var sectorMult = sector.ToLower() switch
            {
                "technology" => 1.3, "energy" => 1.4, "consumer discretionary" => 1.2,
                "consumer staples" => 0.7, "utilities" => 0.6, "healthcare" => 0.9, _ => 1.0
            };
            riskSum += 50 * sectorMult; cnt++;

            var score = cnt > 0 ? riskSum / cnt : 50;
            var level = score switch { >= 70 => "VeryHigh", >= 55 => "High", >= 35 => "Medium", _ => "Low" };

            return Task.FromResult(JsonConvert.SerializeObject(new
            {
                risk_score = Math.Round(score, 1), risk_level = level, risk_factors = factors,
                sector_multiplier = sectorMult
            }, Formatting.Indented));
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    private Task<string> ComputePriceTargetAsync(string input)
    {
        try
        {
            var p      = JObject.Parse(input);
            var cur    = p["current_price"]?.Value<double>() ?? 0;
            var eps    = p["eps"]?.Value<double?>();
            var epsG   = p["eps_growth"]?.Value<double?>();
            var pe     = p["pe_ratio"]?.Value<double?>();
            var action = p["action"]?.ToString() ?? "Hold";

            var mult = action switch
            {
                "StrongBuy"  => 1.25, "Buy"  => 1.13,
                "Hold"       => 1.02, "Sell" => 0.90,
                "StrongSell" => 0.78, _      => 1.00
            };

            double? earningsTarget = null;
            if (eps.HasValue && epsG.HasValue && pe.HasValue)
            {
                var fwdEps = eps.Value * (1 + epsG.Value);
                earningsTarget = fwdEps * (pe.Value * 0.95);
            }

            var multipleTarget = cur * mult;
            var finalTarget    = earningsTarget.HasValue
                ? (earningsTarget.Value + multipleTarget) / 2
                : multipleTarget;

            var upside = cur > 0 ? ((finalTarget - cur) / cur) * 100 : 0;

            return Task.FromResult(JsonConvert.SerializeObject(new
            {
                current_price         = cur,
                multiple_based_target = Math.Round(multipleTarget, 2),
                earnings_based_target = earningsTarget.HasValue ? Math.Round(earningsTarget.Value, 2) : (double?)null,
                final_price_target    = Math.Round(finalTarget, 2),
                upside_percent        = Math.Round(upside, 1)
            }, Formatting.Indented));
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    // ── HTTP Helpers ──────────────────────────────────────────────────────

    /// <summary>Fetch URL and parse as JSON. Adds required headers for Yahoo Finance API.</summary>
    private async Task<JObject?> FetchJsonAsync(string url)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            request.Headers.Add("Accept", "application/json,text/plain,*/*");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            request.Headers.Add("Origin", "https://finance.yahoo.com");
            request.Headers.Add("Referer", "https://finance.yahoo.com/");

            var resp = await _http.SendAsync(request);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("HTTP {Status} for {Url}", (int)resp.StatusCode, url);
                return null;
            }
            var body = await resp.Content.ReadAsStringAsync();
            return JObject.Parse(body);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "FetchJson failed: {Url}", url);
            return null;
        }
    }

    /// <summary>Fetch URL and return raw string (for RSS/HTML).</summary>
    private async Task<string?> FetchRawAsync(string url)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,*/*;q=0.8");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.5");

            var resp = await _http.SendAsync(request);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync() : null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "FetchRaw failed: {Url}", url);
            return null;
        }
    }

    private static string ExtractTicker(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        // Take only first line to strip any LLM commentary appended after JSON
        var firstLine = input.Split('\n')[0].Trim();

        try
        {
            var obj = JObject.Parse(firstLine);
            // Handle both "ticker" and "TICKER" keys
            var ticker = obj["ticker"]?.ToString()
                      ?? obj["TICKER"]?.ToString()
                      ?? obj["symbol"]?.ToString()
                      ?? obj["SYMBOL"]?.ToString();
            if (!string.IsNullOrEmpty(ticker))
                return CleanTicker(ticker);
        }
        catch { }

        // Try full input as JSON
        try
        {
            var obj = JObject.Parse(input);
            var ticker = obj["ticker"]?.ToString()
                      ?? obj["TICKER"]?.ToString()
                      ?? obj["symbol"]?.ToString();
            if (!string.IsNullOrEmpty(ticker))
                return CleanTicker(ticker);
        }
        catch { }

        // Plain string fallback — take first word, strip punctuation
        return CleanTicker(firstLine);
    }

    private static string CleanTicker(string raw)
    {
        // Remove quotes, braces, spaces, newlines and any non-alphanumeric except dot/dash
        var cleaned = new string(raw.Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '-').ToArray());
        return cleaned.ToUpper().Trim();
    }
}
