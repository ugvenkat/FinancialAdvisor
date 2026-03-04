namespace FinancialAdvisor.Models;

// ═══════════════════════════════════════════════════════════════
//  AGENTIC MODELS  —  ReAct loop primitives
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// A single step in an agent's ReAct loop.
/// Thought → Action → Observation → (repeat) → Final Answer
/// </summary>
public class AgentStep
{
    public int        StepNumber  { get; set; }
    public string     Thought     { get; set; } = "";   // LLM reasoning
    public string     Action      { get; set; } = "";   // tool name chosen
    public string     ActionInput { get; set; } = "";   // tool arguments (JSON)
    public string     Observation { get; set; } = "";   // tool result
    public bool       IsFinal     { get; set; }         // true = agent is done
    public string     FinalAnswer { get; set; } = "";   // concluding output
    public DateTime   Timestamp   { get; set; } = DateTime.UtcNow;
}

/// <summary>Full trace of an agent's reasoning across all steps.</summary>
public class AgentTrace
{
    public string          AgentName   { get; set; } = "";
    public string          Ticker      { get; set; } = "";
    public string          Goal        { get; set; } = "";
    public List<AgentStep> Steps       { get; set; } = new();
    public string          FinalAnswer { get; set; } = "";
    public bool            Succeeded   { get; set; }
    public int             TotalSteps  => Steps.Count;
}

/// <summary>A tool the agent can invoke.</summary>
public class AgentTool
{
    public string Name        { get; set; } = "";
    public string Description { get; set; } = "";
    public string Parameters  { get; set; } = "";   // JSON schema description
}

// ═══════════════════════════════════════════════════════════════
//  JOB / REQUEST MODELS
// ═══════════════════════════════════════════════════════════════

public class AnalysisRequest
{
    public List<string> Tickers      { get; set; } = new();
    public bool         ForceRefresh { get; set; } = false;
    public string?      AnalystNotes { get; set; }
}

public class AnalysisJob
{
    public string           JobId         { get; set; } = Guid.NewGuid().ToString("N")[..8].ToUpper();
    public List<string>     Tickers       { get; set; } = new();
    public JobStatus        Status        { get; set; } = JobStatus.Queued;
    public DateTime         CreatedAt     { get; set; } = DateTime.UtcNow;
    public DateTime?        CompletedAt   { get; set; }
    public List<StockReport> Reports      { get; set; } = new();
    public string?          ErrorMessage  { get; set; }
    public string?          OutputDir     { get; set; }
    // Tickers that failed during processing, with their error reason.
    public Dictionary<string, string> FailedTickers { get; set; } = new();
}

public enum JobStatus { Queued, Running, Completed, Failed }

// ═══════════════════════════════════════════════════════════════
//  RAW DATA MODELS  (populated by DataCollectionAgent tools)
// ═══════════════════════════════════════════════════════════════

public class StockRawData
{
    public string              Ticker         { get; set; } = "";
    public string              CompanyName    { get; set; } = "";
    public FinancialMetrics    Metrics        { get; set; } = new();
    public List<NewsArticle>   News           { get; set; } = new();
    public List<AnalystRating> AnalystRatings { get; set; } = new();
    public EarningsData        Earnings       { get; set; } = new();
    public DateTime            CollectedAt    { get; set; } = DateTime.UtcNow;
}

public class NewsArticle
{
    public string   Title       { get; set; } = "";
    public string   Summary     { get; set; } = "";
    public string   Source      { get; set; } = "";
    public string   Url         { get; set; } = "";
    public DateTime PublishedAt { get; set; }
}

public class FinancialMetrics
{
    public decimal? CurrentPrice      { get; set; }
    public decimal? MarketCap         { get; set; }
    public decimal? PERatio           { get; set; }
    public decimal? PBRatio           { get; set; }
    public decimal? EPS               { get; set; }
    public decimal? EPSGrowthYoY      { get; set; }
    public decimal? RevenueGrowthYoY  { get; set; }
    public decimal? DebtToEquity      { get; set; }
    public decimal? FreeCashFlow      { get; set; }
    public decimal? Beta              { get; set; }
    public decimal? DividendYield     { get; set; }
    public decimal? FiftyTwoWeekHigh  { get; set; }
    public decimal? FiftyTwoWeekLow   { get; set; }
    public string   Sector            { get; set; } = "";
    public string   Industry          { get; set; } = "";
}

public class AnalystRating
{
    public string   Firm        { get; set; } = "";
    public string   Rating      { get; set; } = "";
    public decimal? PriceTarget { get; set; }
    public DateTime Date        { get; set; }
}

public class EarningsData
{
    public decimal?           LastEPS             { get; set; }
    public decimal?           EstimatedEPS        { get; set; }
    public decimal?           EPSSurprisePercent  { get; set; }
    public decimal?           LastRevenue         { get; set; }
    public string?            NextEarningsDate    { get; set; }
    public List<QuarterlyResult> QuarterlyHistory { get; set; } = new();
}

public class QuarterlyResult
{
    public string   Quarter { get; set; } = "";
    public decimal? EPS     { get; set; }
    public decimal? Revenue { get; set; }
}

// ═══════════════════════════════════════════════════════════════
//  AGENT OUTPUT MODELS
// ═══════════════════════════════════════════════════════════════

public class FundamentalScore
{
    public string       Ticker               { get; set; } = "";
    public double       Score                { get; set; }
    public string       Grade                { get; set; } = "";
    public List<string> Strengths            { get; set; } = new();
    public List<string> Weaknesses           { get; set; } = new();
    public string       DetailedAnalysis     { get; set; } = "";
    public AgentTrace   Trace                { get; set; } = new();
}

public class SentimentScore
{
    public string          Ticker           { get; set; } = "";
    public SentimentClass  Overall          { get; set; }
    public double          Score            { get; set; }   // -1 to +1
    public double          BullishPercent   { get; set; }
    public double          NeutralPercent   { get; set; }
    public double          BearishPercent   { get; set; }
    public string          SentimentSummary { get; set; } = "";
    public List<SentimentItem> Items        { get; set; } = new();
    public AgentTrace      Trace            { get; set; } = new();
}

public enum SentimentClass { Bullish, Neutral, Bearish }

public class SentimentItem
{
    public string         Source     { get; set; } = "";
    public string         Headline   { get; set; } = "";
    public SentimentClass Sentiment  { get; set; }
    public double         Confidence { get; set; }
}

public class RiskScore
{
    public string       Ticker        { get; set; } = "";
    public RiskLevel    Level         { get; set; }
    public double       Score         { get; set; }   // 0-100
    public List<string> RiskFactors   { get; set; } = new();
    public string       RiskSummary   { get; set; } = "";
    public AgentTrace   Trace         { get; set; } = new();
}

public enum RiskLevel { Low, Medium, High, VeryHigh }

public class InvestmentRecommendation
{
    public string       Ticker          { get; set; } = "";
    public string       CompanyName     { get; set; } = "";
    public Action       Action          { get; set; }
    public double       Confidence      { get; set; }
    public RiskLevel    RiskLevel       { get; set; }
    public decimal?     PriceTarget     { get; set; }
    public decimal?     CurrentPrice    { get; set; }
    public double?      UpsidePercent   { get; set; }
    public string       TimeHorizon     { get; set; } = "6-12 months";
    public string       Rationale       { get; set; } = "";
    public List<string> KeyCatalysts    { get; set; } = new();
    public List<string> KeyRisks        { get; set; } = new();
    public FundamentalScore Fundamental { get; set; } = new();
    public SentimentScore   Sentiment   { get; set; } = new();
    public RiskScore        Risk        { get; set; } = new();
    public string       CIOSummary      { get; set; } = "";
    public AgentTrace   CIOTrace        { get; set; } = new();
}

public enum Action { StrongBuy, Buy, Hold, Sell, StrongSell }

// ═══════════════════════════════════════════════════════════════
//  REPORT MODELS
// ═══════════════════════════════════════════════════════════════

public class StockReport
{
    public string                Ticker         { get; set; } = "";
    public InvestmentRecommendation Recommendation { get; set; } = new();
    public StockRawData          RawData        { get; set; } = new();
    public List<AgentTrace>      AllTraces      { get; set; } = new();
    public DateTime              GeneratedAt    { get; set; } = DateTime.UtcNow;
}

public class PortfolioReport
{
    public string            JobId            { get; set; } = "";
    public DateTime          GeneratedAt      { get; set; } = DateTime.UtcNow;
    public List<StockReport> StockReports     { get; set; } = new();
    public string            ExecutiveSummary { get; set; } = "";
}
