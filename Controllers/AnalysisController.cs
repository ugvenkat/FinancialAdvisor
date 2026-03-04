using Microsoft.AspNetCore.Mvc;
using FinancialAdvisor.Agents;
using FinancialAdvisor.Models;
using FinancialAdvisor.Services;
using FinancialAdvisor.Data;

namespace FinancialAdvisor.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class AnalysisController : ControllerBase
{
    private readonly IOrchestrator      _orchestrator;
    private readonly IMemoryStore       _memory;
    private readonly AgentStatusTracker _tracker;

    public AnalysisController(IOrchestrator orchestrator, IMemoryStore memory, AgentStatusTracker tracker)
    {
        _orchestrator = orchestrator;
        _memory       = memory;
        _tracker      = tracker;
    }

    /// <summary>Start a new agentic analysis job</summary>
    [HttpPost]
    [ProducesResponseType(202)]
    public async Task<IActionResult> Start([FromBody] AnalysisRequest req)
    {
        if (req.Tickers == null || !req.Tickers.Any())
            return BadRequest(new { code = "INVALID_REQUEST", message = "At least one ticker required" });
        if (req.Tickers.Count > 10)
            return BadRequest(new { code = "INVALID_REQUEST", message = "Max 10 tickers per job" });

        var job = await _orchestrator.StartJobAsync(req);
        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        return Accepted(new
        {
            job.JobId,
            Status        = job.Status.ToString(),
            job.Tickers,
            job.CreatedAt,
            StatusUrl     = $"{baseUrl}/api/analysis/{job.JobId}",
            LiveStatusUrl = $"{baseUrl}/api/analysis/{job.JobId}/live",
            Message       = $"5-agent agentic analysis started for: {string.Join(", ", job.Tickers)}"
        });
    }

    /// <summary>Poll job status and get full results</summary>
    [HttpGet("{jobId}")]
    public async Task<IActionResult> Get(string jobId)
    {
        var job = await _orchestrator.GetJobAsync(jobId);
        if (job == null) return NotFound(new { code = "JOB_NOT_FOUND", message = $"Job '{jobId}' not found" });

        return Ok(new
        {
            job.JobId,
            Status    = job.Status.ToString(),
            job.Tickers,
            job.CreatedAt,
            job.CompletedAt,
            Duration  = job.CompletedAt.HasValue
                ? Math.Round((job.CompletedAt.Value - job.CreatedAt).TotalSeconds, 1)
                : Math.Round((DateTime.UtcNow - job.CreatedAt).TotalSeconds, 1),
            job.ErrorMessage,
            job.FailedTickers,
            OutputDir = job.OutputDir,
            Reports   = job.Reports.Select(r => new
            {
                r.Ticker,
                r.GeneratedAt,
                TotalAgentSteps = r.AllTraces.Sum(t => t.TotalSteps),
                Recommendation  = new
                {
                    r.Recommendation.Action,
                    r.Recommendation.Confidence,
                    r.Recommendation.RiskLevel,
                    r.Recommendation.CurrentPrice,
                    r.Recommendation.PriceTarget,
                    r.Recommendation.UpsidePercent,
                    r.Recommendation.TimeHorizon,
                    r.Recommendation.KeyCatalysts,
                    r.Recommendation.KeyRisks,
                    CIOSummary  = string.IsNullOrEmpty(r.Recommendation.CIOSummary)
                        ? r.Recommendation.CIOSummary
                        : (r.Recommendation.CIOSummary.Length > 500 ? r.Recommendation.CIOSummary[..500] : r.Recommendation.CIOSummary),
                    Fundamental = new { r.Recommendation.Fundamental.Score, r.Recommendation.Fundamental.Grade, r.Recommendation.Fundamental.Strengths, r.Recommendation.Fundamental.Weaknesses },
                    Sentiment   = new { r.Recommendation.Sentiment.Overall, r.Recommendation.Sentiment.Score, r.Recommendation.Sentiment.BullishPercent, r.Recommendation.Sentiment.BearishPercent },
                    Risk        = new { r.Recommendation.Risk.Level, r.Recommendation.Risk.Score, r.Recommendation.Risk.RiskFactors }
                }
            })
        });
    }

    /// <summary>
    /// LIVE DEBUG — see exactly what each agent is doing RIGHT NOW.
    /// Poll this every 5 seconds while a job is running to watch agent progress.
    /// </summary>
    [HttpGet("{jobId}/live")]
    public async Task<IActionResult> LiveStatus(string jobId)
    {
        var job     = await _memory.GetJobAsync(jobId);
        if (job == null) return NotFound(new { code = "JOB_NOT_FOUND", message = $"Job '{jobId}' not found" });

        var status  = _tracker.Get(jobId);
        var elapsed = Math.Round((DateTime.UtcNow - job.CreatedAt).TotalSeconds, 1);

        return Ok(new
        {
            jobId,
            jobStatus    = job.Status.ToString(),
            elapsedSecs  = elapsed,
            lastUpdate   = status?.LastUpdate,
            activeAgents = status?.ActiveAgents.Values
                .OrderBy(a => a.Agent)
                .Select(a => new
                {
                    a.Ticker,
                    a.Agent,
                    a.Step,
                    a.Activity,
                    a.Completed,
                    stuckForSecs = Math.Round(a.SecondsSinceUpdate, 0)
                }),
            hint = status == null
                ? "No live data yet — job may not have started"
                : $"Last activity {Math.Round((DateTime.UtcNow - status.LastUpdate).TotalSeconds, 0)}s ago"
        });
    }

    /// <summary>Get full agent reasoning traces (shows HOW agents decided)</summary>
    [HttpGet("{jobId}/traces")]
    public async Task<IActionResult> GetTraces(string jobId)
    {
        var job = await _memory.GetJobAsync(jobId);
        if (job == null) return NotFound(new { code = "JOB_NOT_FOUND", message = $"Job '{jobId}' not found" });

        var traces = await _memory.GetTracesAsync(jobId);
        return Ok(new
        {
            jobId,
            TotalAgents = traces.Select(t => t.AgentName).Distinct().Count(),
            TotalSteps  = traces.Sum(t => t.Trace.TotalSteps),
            Traces      = traces.GroupBy(t => t.Ticker).Select(g => new
            {
                Ticker = g.Key,
                Agents = g.Select(t => new
                {
                    t.AgentName,
                    TotalSteps = t.Trace.TotalSteps,
                    Succeeded  = t.Trace.Succeeded,
                    Steps      = t.Trace.Steps.Select(s => new
                    {
                        s.StepNumber,
                        s.Thought,
                        s.Action,
                        s.ActionInput,
                        Observation = string.IsNullOrEmpty(s.Observation)
                            ? s.Observation
                            : (s.Observation.Length > 200 ? s.Observation[..200] + "..." : s.Observation),
                        s.IsFinal,
                        FinalAnswer = s.IsFinal && !string.IsNullOrEmpty(s.FinalAnswer)
                            ? s.FinalAnswer[..Math.Min(300, s.FinalAnswer.Length)] : null
                    })
                })
            })
        });
    }

    /// <summary>List recent jobs</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int limit = 10)
    {
        var jobs = await _orchestrator.GetRecentJobsAsync(Math.Min(limit, 50));
        return Ok(jobs.Select(j => new
        {
            j.JobId,
            Status   = j.Status.ToString(),
            j.Tickers,
            j.CreatedAt,
            j.CompletedAt,
            Reports  = j.Reports.Count,
            j.OutputDir
        }));
    }

    /// <summary>Download Markdown report</summary>
    [HttpGet("{jobId}/report")]
    [Produces("text/markdown")]
    public async Task<IActionResult> Report(string jobId)
    {
        var job = await _memory.GetJobAsync(jobId);
        if (job == null) return NotFound(new { code = "JOB_NOT_FOUND", message = $"Job '{jobId}' not found" });
        if (job.Status != JobStatus.Completed) return BadRequest(new { code = "JOB_NOT_COMPLETE", message = $"Job is {job.Status} — not ready yet" });

        var path = Path.Combine(job.OutputDir ?? "", "report.md");
        if (!System.IO.File.Exists(path)) return NotFound(new { code = "REPORT_MISSING", message = "Report file not found on disk" });
        return Content(await System.IO.File.ReadAllTextAsync(path), "text/markdown");
    }

    /// <summary>Health check</summary>
    [HttpGet("~/api/health")]
    public IActionResult Health() => Ok(new
    {
        status    = "healthy",
        timestamp = DateTime.UtcNow,
        pattern   = "ReAct (Reason - Act - Observe)",
        agents    = new[]
        {
            "1 DataCollectionAgent",
            "2 FundamentalAnalysisAgent",
            "3 SentimentAnalysisAgent",
            "4 RiskAnalysisAgent",
            "5 CIOAgent"
        }
    });
}
