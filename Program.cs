using Serilog;
using FinancialAdvisor.Agents;
using FinancialAdvisor.Data;
using FinancialAdvisor.Services;
using FinancialAdvisor.Tools;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/advisor-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

builder.Services.AddControllers().AddNewtonsoftJson();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title       = "Agentic Financial Advisor API",
        Version     = "v1",
        Description = "5 autonomous ReAct agents"
    });
});

// Ollama
builder.Services.AddHttpClient<IOllamaService, OllamaService>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434");
    c.Timeout     = TimeSpan.FromMinutes(10);
});

// Web scraper
builder.Services.AddHttpClient<FinancialToolkit>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,*/*;q=0.8");
    c.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");
});

// Infrastructure
builder.Services.AddSingleton<IMemoryStore, SqliteMemoryStore>();
builder.Services.AddSingleton<IReportWriter, ReportWriter>();
builder.Services.AddSingleton<AgentStatusTracker>();   // live status tracker

// ReAct Engine + Agents
builder.Services.AddSingleton<ReActEngine>();
builder.Services.AddTransient<DataCollectionAgent>();
builder.Services.AddTransient<FundamentalAnalysisAgent>();
builder.Services.AddTransient<SentimentAnalysisAgent>();
builder.Services.AddTransient<RiskAnalysisAgent>();
builder.Services.AddTransient<ChiefInvestmentOfficerAgent>();

// Orchestrator
builder.Services.AddSingleton<IOrchestrator, MultiAgentOrchestrator>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

await app.Services.GetRequiredService<IMemoryStore>().InitializeAsync();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Agentic Financial Advisor v1");
        c.DocumentTitle = "Agentic Financial Advisor";
    });
}

app.UseSerilogRequestLogging();
app.UseCors();
app.UseAuthorization();
app.MapControllers();

Log.Information("""

==============================================================
  Agentic Financial Investment Advisor - NET 8 WebAPI
  Pattern: ReAct (Reason - Act - Observe - Repeat)
  API:     http://localhost:5000
  Swagger: http://localhost:5000/swagger
  
  DEBUG ENDPOINTS:
  Live status:  GET /api/analysis/{jobId}/live
  Agent traces: GET /api/analysis/{jobId}/traces
  Log file:     logs/advisor-YYYYMMDD.log
==============================================================
""");

app.Run();
