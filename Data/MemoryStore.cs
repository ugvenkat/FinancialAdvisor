using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using FinancialAdvisor.Models;

namespace FinancialAdvisor.Data;

public interface IMemoryStore
{
    Task InitializeAsync();
    Task SaveJobAsync(AnalysisJob job);
    Task<AnalysisJob?> GetJobAsync(string jobId);
    Task<List<AnalysisJob>> GetRecentJobsAsync(int limit = 20);
    Task SaveReportAsync(string jobId, StockReport report);
    Task<List<AgentTraceLog>> GetTracesAsync(string jobId);
    Task SaveTraceAsync(string jobId, string ticker, string agentName, AgentTrace trace);
}

public class AgentTraceLog
{
    public int        Id        { get; set; }
    public string     JobId     { get; set; } = "";
    public string     Ticker    { get; set; } = "";
    public string     AgentName { get; set; } = "";
    public AgentTrace Trace     { get; set; } = new();
    public DateTime   SavedAt   { get; set; }
}

public class SqliteMemoryStore : IMemoryStore
{
    private readonly string _conn;
    private readonly ILogger<SqliteMemoryStore> _log;

    public SqliteMemoryStore(IConfiguration cfg, ILogger<SqliteMemoryStore> log)
    {
        var path = cfg["Database:Path"] ?? "data/financial_advisor.db";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _conn = $"Data Source={path}";
        _log  = log;
    }

    public async Task InitializeAsync()
    {
        await using var c = new SqliteConnection(_conn);
        await c.OpenAsync();
        await Exec(c, """
            CREATE TABLE IF NOT EXISTS jobs (
                job_id TEXT PRIMARY KEY, data_json TEXT NOT NULL, created_at TEXT NOT NULL, status TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS reports (
                id INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL,
                ticker TEXT NOT NULL, data_json TEXT NOT NULL, generated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS agent_traces (
                id INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL,
                ticker TEXT NOT NULL, agent_name TEXT NOT NULL,
                trace_json TEXT NOT NULL, saved_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_reports_job ON reports(job_id);
            CREATE INDEX IF NOT EXISTS idx_traces_job  ON agent_traces(job_id);
        """);
        _log.LogInformation("SQLite store initialized");
    }

    public async Task SaveJobAsync(AnalysisJob job)
    {
        await using var c = new SqliteConnection(_conn);
        await c.OpenAsync();
        await Exec(c, """
            INSERT INTO jobs(job_id,data_json,created_at,status) VALUES(@id,@j,@ts,@s)
            ON CONFLICT(job_id) DO UPDATE SET data_json=excluded.data_json, status=excluded.status
            """,
            ("@id", job.JobId), ("@j", JsonConvert.SerializeObject(job)),
            ("@ts", job.CreatedAt.ToString("O")), ("@s", job.Status.ToString()));
    }

    public async Task<AnalysisJob?> GetJobAsync(string jobId)
    {
        await using var c = new SqliteConnection(_conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data_json FROM jobs WHERE job_id=@id";
        cmd.Parameters.AddWithValue("@id", jobId);
        var r = await cmd.ExecuteScalarAsync();
        return r is string json ? JsonConvert.DeserializeObject<AnalysisJob>(json) : null;
    }

    public async Task<List<AnalysisJob>> GetRecentJobsAsync(int limit = 20)
    {
        await using var c = new SqliteConnection(_conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data_json FROM jobs ORDER BY created_at DESC LIMIT @l";
        cmd.Parameters.AddWithValue("@l", limit);
        var jobs = new List<AnalysisJob>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var j = JsonConvert.DeserializeObject<AnalysisJob>(reader.GetString(0));
            if (j != null) jobs.Add(j);
        }
        return jobs;
    }

    public async Task SaveReportAsync(string jobId, StockReport report)
    {
        await using var c = new SqliteConnection(_conn);
        await c.OpenAsync();
        await Exec(c, "INSERT INTO reports(job_id,ticker,data_json,generated_at) VALUES(@j,@t,@d,@ts)",
            ("@j", jobId), ("@t", report.Ticker),
            ("@d", JsonConvert.SerializeObject(report)), ("@ts", report.GeneratedAt.ToString("O")));
    }

    public async Task SaveTraceAsync(string jobId, string ticker, string agentName, AgentTrace trace)
    {
        await using var c = new SqliteConnection(_conn);
        await c.OpenAsync();
        await Exec(c, "INSERT INTO agent_traces(job_id,ticker,agent_name,trace_json,saved_at) VALUES(@j,@t,@a,@tr,@ts)",
            ("@j", jobId), ("@t", ticker), ("@a", agentName),
            ("@tr", JsonConvert.SerializeObject(trace)), ("@ts", DateTime.UtcNow.ToString("O")));
    }

    public async Task<List<AgentTraceLog>> GetTracesAsync(string jobId)
    {
        await using var c = new SqliteConnection(_conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,job_id,ticker,agent_name,trace_json,saved_at FROM agent_traces WHERE job_id=@j ORDER BY id";
        cmd.Parameters.AddWithValue("@j", jobId);
        var logs = new List<AgentTraceLog>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            logs.Add(new AgentTraceLog
            {
                Id        = reader.GetInt32(0),
                JobId     = reader.GetString(1),
                Ticker    = reader.GetString(2),
                AgentName = reader.GetString(3),
                Trace     = JsonConvert.DeserializeObject<AgentTrace>(reader.GetString(4)) ?? new(),
                SavedAt   = DateTime.Parse(reader.GetString(5))
            });
        return logs;
    }

    private static async Task Exec(SqliteConnection c, string sql,
        params (string name, object val)[] ps)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}
