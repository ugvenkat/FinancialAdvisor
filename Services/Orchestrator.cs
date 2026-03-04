using FinancialAdvisor.Models;
using FinancialAdvisor.Agents;
using FinancialAdvisor.Data;
using FinancialAdvisor.Services;

namespace FinancialAdvisor.Services;

public interface IOrchestrator
{
    Task<AnalysisJob> StartJobAsync(AnalysisRequest request);
    Task<AnalysisJob?> GetJobAsync(string jobId);
    Task<List<AnalysisJob>> GetRecentJobsAsync(int limit = 10);
}

/// <summary>
/// Orchestrates all 5 agentic agents.
/// Each agent runs its own autonomous ReAct loop.
/// Agents 2-4 run in parallel after Agent 1 completes.
/// Agent 5 synthesizes all outputs.
/// </summary>
public class MultiAgentOrchestrator : IOrchestrator
{
    private readonly DataCollectionAgent          _dataAgent;
    private readonly FundamentalAnalysisAgent     _fundAgent;
    private readonly SentimentAnalysisAgent       _sentAgent;
    private readonly RiskAnalysisAgent            _riskAgent;
    private readonly ChiefInvestmentOfficerAgent  _cioAgent;
    private readonly IMemoryStore                 _memory;
    private readonly IReportWriter                _writer;
    private readonly AgentStatusTracker           _tracker;
    private readonly ILogger<MultiAgentOrchestrator> _log;

    public MultiAgentOrchestrator(
        DataCollectionAgent dataAgent,
        FundamentalAnalysisAgent fundAgent,
        SentimentAnalysisAgent sentAgent,
        RiskAnalysisAgent riskAgent,
        ChiefInvestmentOfficerAgent cioAgent,
        IMemoryStore memory,
        IReportWriter writer,
        AgentStatusTracker tracker,
        ILogger<MultiAgentOrchestrator> log)
    {
        _dataAgent = dataAgent;
        _fundAgent = fundAgent;
        _sentAgent = sentAgent;
        _riskAgent = riskAgent;
        _cioAgent  = cioAgent;
        _memory    = memory;
        _writer    = writer;
        _tracker   = tracker;
        _log       = log;
    }

    public async Task<AnalysisJob> StartJobAsync(AnalysisRequest request)
    {
        var job = new AnalysisJob
        {
            Tickers = request.Tickers.Select(t => t.Trim().ToUpper()).Distinct().ToList(),
            Status  = JobStatus.Queued
        };

        await _memory.SaveJobAsync(job);
        _log.LogInformation("Job {Id} queued: {Tickers}", job.JobId, string.Join(", ", job.Tickers));
        // TODO: store a CancellationTokenSource per job to support CancelJobAsync(jobId) in future.
        _ = Task.Run(() => RunJobAsync(job, request));
        return job;
    }

    public Task<AnalysisJob?> GetJobAsync(string jobId)        => _memory.GetJobAsync(jobId);
    public Task<List<AnalysisJob>> GetRecentJobsAsync(int lim) => _memory.GetRecentJobsAsync(lim);

    private async Task RunJobAsync(AnalysisJob job, AnalysisRequest request)
    {
        job.Status = JobStatus.Running;
        await _memory.SaveJobAsync(job);

        try
        {
            _log.LogInformation("════ Job {Id} START ({Count} tickers) ════", job.JobId, job.Tickers.Count);

            var portfolio = new PortfolioReport { JobId = job.JobId };

            foreach (var ticker in job.Tickers)
            {
                try
                {
                    var report = await ProcessTickerAsync(job.JobId, ticker, default);
                    job.Reports.Add(report);
                    portfolio.StockReports.Add(report);
                    await _memory.SaveReportAsync(job.JobId, report);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to process {Ticker}", ticker);
                    job.FailedTickers[ticker] = ex.Message;
                    await _memory.SaveJobAsync(job);
                }
            }

            portfolio.ExecutiveSummary = BuildSummary(portfolio, job.FailedTickers);
            job.OutputDir    = await _writer.WriteReportAsync(job.JobId, portfolio);
            job.Status       = JobStatus.Completed;
            job.CompletedAt  = DateTime.UtcNow;

            _log.LogInformation("════ Job {Id} COMPLETE in {Sec:F0}s → {Path} ════",
                job.JobId, (job.CompletedAt.Value - job.CreatedAt).TotalSeconds, job.OutputDir);
        }
        catch (Exception ex)
        {
            job.Status       = JobStatus.Failed;
            job.ErrorMessage = ex.Message;
            _log.LogError(ex, "Job {Id} FAILED", job.JobId);
        }

        await _memory.SaveJobAsync(job);
        // Release in-memory tracking state — job is in a terminal state and persisted to SQLite.
        _tracker.Clear(job.JobId);
    }

    private async Task<StockReport> ProcessTickerAsync(string jobId, string ticker, CancellationToken ct)
    {
        _log.LogInformation("▶ [{Ticker}] Pipeline start", ticker);

        // ── Agent 1: Data Collection (autonomous ReAct loop) ──────────────
        var (rawData, dataTrace) = await _dataAgent.CollectAsync(jobId, ticker, ct);
        await _memory.SaveTraceAsync(jobId, ticker, "DataCollectionAgent", dataTrace);
        _log.LogInformation("  ✓ Agent1 DataCollection: {Steps} steps", dataTrace.TotalSteps);

        // ── Agents 2-4: Run in parallel (each with their own ReAct loop) ──
        var fundTask = _fundAgent.AnalyzeAsync(jobId, rawData, ct);
        var sentTask = _sentAgent.AnalyzeAsync(jobId, rawData, ct);
        var riskTask = _riskAgent.AnalyzeAsync(jobId, rawData, ct);

        await Task.WhenAll(fundTask, sentTask, riskTask);

        var (fundamental, fundTrace) = await fundTask;
        var (sentiment,   sentTrace) = await sentTask;
        var (risk,        riskTrace) = await riskTask;

        await _memory.SaveTraceAsync(jobId, ticker, "FundamentalAnalysisAgent", fundTrace);
        await _memory.SaveTraceAsync(jobId, ticker, "SentimentAnalysisAgent",   sentTrace);
        await _memory.SaveTraceAsync(jobId, ticker, "RiskAnalysisAgent",        riskTrace);

        _log.LogInformation("  ✓ Agent2 Fundamental: {Steps} steps, Score={S:F0}, Grade={G}",
            fundTrace.TotalSteps, fundamental.Score, fundamental.Grade);
        _log.LogInformation("  ✓ Agent3 Sentiment: {Steps} steps, {Overall}",
            sentTrace.TotalSteps, sentiment.Overall);
        _log.LogInformation("  ✓ Agent4 Risk: {Steps} steps, {Level}",
            riskTrace.TotalSteps, risk.Level);

        // ── Agent 5: CIO Decision (synthesizes everything, autonomous) ────
        var (recommendation, cioTrace) = await _cioAgent.DecideAsync(
            jobId, rawData, fundamental, sentiment, risk, ct);
        await _memory.SaveTraceAsync(jobId, ticker, "CIOAgent", cioTrace);

        _log.LogInformation("  ✓ Agent5 CIO: {Steps} steps → {Action} ({Conf:F0}%)",
            cioTrace.TotalSteps, recommendation.Action, recommendation.Confidence);

        return new StockReport
        {
            Ticker         = ticker,
            Recommendation = recommendation,
            RawData        = rawData,
            AllTraces      = new() { dataTrace, fundTrace, sentTrace, riskTrace, cioTrace },
            GeneratedAt    = DateTime.UtcNow
        };
    }

    private static string BuildSummary(PortfolioReport report, Dictionary<string, string> failedTickers)
    {
        var buys  = report.StockReports.Count(r => r.Recommendation.Action is Models.Action.Buy or Models.Action.StrongBuy);
        var holds = report.StockReports.Count(r => r.Recommendation.Action is Models.Action.Hold);
        var sells = report.StockReports.Count(r => r.Recommendation.Action is Models.Action.Sell or Models.Action.StrongSell);
        var best  = report.StockReports
            .Where(r => r.Recommendation.Action is Models.Action.Buy or Models.Action.StrongBuy)
            .OrderByDescending(r => r.Recommendation.Confidence)
            .FirstOrDefault();

        var summary = $"Analyzed {report.StockReports.Count} stock(s): {buys} Buy, {holds} Hold, {sells} Sell. " +
                      (best != null ? $"Top pick: {best.Ticker} ({best.Recommendation.Action}, {best.Recommendation.Confidence:F0}% confidence)." : "");

        if (failedTickers.Any())
            summary += $" Failed: {string.Join(", ", failedTickers.Keys)}.";

        return summary;
    }
}
