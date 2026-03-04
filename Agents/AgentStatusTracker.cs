namespace FinancialAdvisor.Agents;

/// <summary>
/// Thread-safe in-memory tracker of what each agent is doing RIGHT NOW.
/// Used by the /api/analysis/{id}/status endpoint for live debugging.
/// </summary>
public class AgentStatusTracker
{
    private readonly Dictionary<string, LiveJobStatus> _jobs = new();
    private readonly object _lock = new();

    public void Update(string jobId, string ticker, string agent, int step, string activity)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                job = new LiveJobStatus { JobId = jobId };
                _jobs[jobId] = job;
            }

            var key = $"{ticker}::{agent}";
            job.ActiveAgents[key] = new AgentActivity
            {
                Ticker     = ticker,
                Agent      = agent,
                Step       = step,
                Activity   = activity,
                UpdatedAt  = DateTime.UtcNow
            };

            job.LastUpdate = DateTime.UtcNow;
        }
    }

    public void Complete(string jobId, string ticker, string agent, string result)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out var job)) return;
            var key = $"{ticker}::{agent}";
            if (job.ActiveAgents.TryGetValue(key, out var act))
            {
                act.Activity  = $"✅ DONE: {result}";
                act.Completed = true;
                act.UpdatedAt = DateTime.UtcNow;
            }
        }
    }

    public LiveJobStatus? Get(string jobId)
    {
        lock (_lock)
        {
            return _jobs.TryGetValue(jobId, out var j) ? j : null;
        }
    }

    /// <summary>Remove a completed job's tracking state to prevent unbounded memory growth.</summary>
    public void Clear(string jobId)
    {
        lock (_lock) _jobs.Remove(jobId);
    }
}

public class LiveJobStatus
{
    public string JobId { get; set; } = "";
    public DateTime LastUpdate { get; set; } = DateTime.UtcNow;
    public Dictionary<string, AgentActivity> ActiveAgents { get; set; } = new();
}

public class AgentActivity
{
    public string   Ticker    { get; set; } = "";
    public string   Agent     { get; set; } = "";
    public int      Step      { get; set; }
    public string   Activity  { get; set; } = "";
    public bool     Completed { get; set; }
    public DateTime UpdatedAt { get; set; }
    public double   SecondsSinceUpdate => (DateTime.UtcNow - UpdatedAt).TotalSeconds;
}
