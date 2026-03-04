# 🤖 Agentic Financial Advisor

A **.NET 8 WebAPI** that uses **5 autonomous AI agents** in a **ReAct (Reason–Act–Observe)** loop to analyze stocks and produce investment recommendations. Each agent reasons independently, calls real financial data tools, and synthesizes findings into a Buy / Hold / Sell decision with a price target and confidence score.

---

## Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Agent Pipeline](#agent-pipeline)
- [ReAct Engine](#react-engine)
- [Financial Tools](#financial-tools)
- [API Endpoints](#api-endpoints)
- [Project Structure](#project-structure)
- [Configuration](#configuration)
- [Getting Started](#getting-started)
- [Output & Reports](#output--reports)
- [Dependencies](#dependencies)
- [Known Limitations](#known-limitations)

---

## Overview

The system accepts a list of stock tickers (e.g. `["AAPL", "MSFT"]`), fires off a background job, and runs each ticker through a 5-agent pipeline. Agents 1–5 each run their own autonomous ReAct loop against a local Ollama LLM, calling Yahoo Finance APIs as tools. The final output is a structured investment recommendation with full agent reasoning traces.

**Key characteristics:**
- Fully agentic — each agent decides *which* tools to call and *when* to stop
- Parallel execution — Agents 2, 3 and 4 run simultaneously via `Task.WhenAll`
- Observable — live status endpoint + full reasoning traces saved to disk
- Local-first — runs entirely on your machine via Ollama (no OpenAI key needed)

---

## Architecture

```
Client (Swagger / App)
        │
        │  POST /api/analysis  { tickers: ["AAPL"] }
        ▼
AnalysisController
        │
        │  validates → 202 Accepted → Task.Run (background)
        ▼
MultiAgentOrchestrator
        │
        ├── SaveJobAsync → SQLite (status: Queued → Running)
        │
        │  Per ticker:
        ├─► Agent 1: DataCollectionAgent       (sequential)
        │
        ├─► Agent 2: FundamentalAnalysisAgent  ─┐
        ├─► Agent 3: SentimentAnalysisAgent    ─┤ Task.WhenAll (parallel)
        ├─► Agent 4: RiskAnalysisAgent         ─┘
        │
        └─► Agent 5: ChiefInvestmentOfficerAgent (sequential)
                │
                ├── SaveReportAsync → SQLite
                ├── WriteReportAsync → ./reports/{jobId}/
                └── SaveJobAsync (status: Completed)
```

All five agents share a single **ReActEngine** and call **OllamaService** (`llama3.1:8b`) for reasoning. Data is fetched from **Yahoo Finance** JSON APIs and MarketWatch. All job state, reports and agent traces are persisted in **SQLite**.

---

## Agent Pipeline

### Agent 1 — DataCollectionAgent
Autonomously gathers all market data for a ticker before the analysis agents run.

- Fetches: current price, P/E, market cap, beta, 52-week range
- Fetches: P/B ratio, debt-to-equity, free cash flow, EPS growth
- Fetches: 8–10 latest news headlines (Yahoo Finance RSS + MarketWatch)
- Fetches: analyst ratings (Buy/Hold/Sell consensus)
- Fetches: earnings data (EPS actuals vs estimates, revenue)
- Output: `StockRawData` model passed to all subsequent agents

### Agent 2 — FundamentalAnalysisAgent *(parallel)*
Scores the company's financial health on a 0–100 scale.

- Grades: A (≥85) / B (≥70) / C (≥55) / D (≥40) / F
- Identifies top 3 strengths and top 3 weaknesses with supporting data
- Considers sector context (e.g. "is this P/E reasonable for tech?")
- Output: `FundamentalScore { Score, Grade, Strengths, Weaknesses, DetailedAnalysis }`

### Agent 3 — SentimentAnalysisAgent *(parallel)*
Classifies news sentiment as Bullish / Neutral / Bearish.

- Classifies each headline individually
- Produces Bullish%, Neutral%, Bearish% breakdown and an overall score (−1 to +1)
- Falls back to keyword matching if LLM JSON parsing fails
- Output: `SentimentScore { Overall, Score, BullishPercent, BearishPercent, Items }`

### Agent 4 — RiskAnalysisAgent *(parallel)*
Computes a risk score (0–100, higher = riskier) and risk level.

- Inputs: beta, P/E valuation, debt-to-equity, sector, 52-week position
- Risk levels: Low / Medium / High / VeryHigh
- Identifies top 3–5 specific risk factors
- Output: `RiskScore { Level, Score, RiskFactors, RiskSummary }`

### Agent 5 — ChiefInvestmentOfficerAgent
Synthesizes all three agent outputs into a final investment decision.

- Weighs conflicting signals (e.g. strong fundamentals vs high risk)
- Calls `compute_price_target` tool with EPS + growth + P/E
- Issues: Action (StrongBuy / Buy / Hold / Sell / StrongSell), Confidence %, Price Target, Upside %, Time Horizon, Key Catalysts, Key Risks
- Output: `InvestmentRecommendation`

---

## ReAct Engine

All agents share a single `ReActEngine` that implements the **Reason–Act–Observe** loop:

```
Thought:       [one sentence of reasoning]
Action:        [exact tool name]
Action Input:  { "ticker": "AAPL" }
               ↓
         tool executes
               ↓
Observation:   [tool result, capped at 3000 chars]
               ↓
         back to Thought...
               ↓
FINAL ANSWER:  [structured response when done]
```

**Constraints per agent run:**

| Parameter | Value |
|---|---|
| Max steps | 6 |
| LLM timeout | 180 seconds |
| Tool timeout | 30 seconds |
| Max observation length | 3 000 characters |
| LLM temperature | 0.1 |
| Max tokens | 2 048 |

**Robustness features:**
- Multi-level response parsing: JSON block → regex → plain text fallback
- `SanitizeActionInput` strips LLM hallucinations after closing `}`
- Unknown tool calls return a descriptive observation instead of crashing
- LLM errors return `FINAL ANSWER: LLM call failed — {reason}` so the loop terminates cleanly
- `FinalAnswer` is always non-null on return (`??= string.Empty` guarantee)
- Markdown code fence stripping before JSON parse

---

## Financial Tools

Ten tools are available across agents, all backed by real APIs:

| Tool | Source | Returns |
|---|---|---|
| `get_stock_price` | Yahoo Finance v8 `/chart` | Price, 52W range, market cap, P/E, beta, EPS, dividend |
| `get_financial_ratios` | Yahoo Finance v11 `/quoteSummary` | P/B, D/E, FCF, revenue growth, EPS growth |
| `get_latest_news` | Yahoo Finance v2 RSS | 10 headlines with title, source, link |
| `get_analyst_ratings` | Yahoo Finance v11 | Buy/Hold/Sell consensus counts |
| `get_earnings_data` | Yahoo Finance v11 | EPS actual vs estimate, revenue, surprise % |
| `get_marketwatch_news` | MarketWatch HTML scrape | Additional news headlines |
| `classify_sentiment` | Keyword classifier | Bullish / Neutral / Bearish per text |
| `calculate_fundamental_score` | Internal calculation | Score 0–100, grade A–F |
| `calculate_risk_score` | Internal calculation | Risk score 0–100, risk level |
| `compute_price_target` | Internal DCF-style formula | Price target, upside % |

Each agent only receives the tools relevant to its role — CIO only gets `compute_price_target`; DataCollectionAgent only gets the six data-fetching tools.

---

## API Endpoints

### Start a job
```http
POST /api/analysis
Content-Type: application/json

{
  "tickers": ["AAPL", "MSFT"],
  "forceRefresh": false,
  "analystNotes": "Focus on AI exposure"
}
```
Returns `202 Accepted` immediately with `jobId`, `statusUrl` and `liveStatusUrl`.
Maximum 10 tickers per job.

### Poll job status & results
```http
GET /api/analysis/{jobId}
```
Returns job status, all reports, recommendations, and `failedTickers` if any tickers errored.

### Live agent status *(poll while running)*
```http
GET /api/analysis/{jobId}/live
```
Returns what each agent is doing right now — current step, activity, and seconds since last update. Poll every 5 seconds to watch reasoning in real time.

### Full reasoning traces
```http
GET /api/analysis/{jobId}/traces
```
Returns every Thought / Action / Observation step each agent took, grouped by ticker and agent. Useful for understanding *why* a recommendation was made.

### Download Markdown report
```http
GET /api/analysis/{jobId}/report
```
Returns the full `report.md` file as `text/markdown`.

### List recent jobs
```http
GET /api/analysis?limit=10
```

### Health check
```http
GET /api/health
```

### Swagger UI
```
http://localhost:5000/swagger
```
Available in Development environment.

---

## Error Response Shape

All errors return a consistent `{ code, message }` payload:

```json
{ "code": "JOB_NOT_FOUND",   "message": "Job 'ABC123' not found" }
{ "code": "INVALID_REQUEST",  "message": "Max 10 tickers per job" }
{ "code": "JOB_NOT_COMPLETE", "message": "Job is Running — not ready yet" }
{ "code": "REPORT_MISSING",   "message": "Report file not found on disk" }
```

---

## Project Structure

```
FinancialAdvisor/
├── Agents/
│   ├── ReActEngine.cs              # Shared Thought→Action→Observe loop
│   ├── AgentStatusTracker.cs       # Thread-safe live status (cleared after job)
│   ├── DataCollectionAgent.cs      # Agent 1 — market data
│   ├── FundamentalAnalysisAgent.cs # Agent 2 — fundamental scoring
│   ├── SentimentAnalysisAgent.cs   # Agent 3 — news sentiment
│   ├── RiskAnalysisAgent.cs        # Agent 4 — risk scoring
│   └── CIOAgent.cs                 # Agent 5 — final decision
│
├── Controllers/
│   └── AnalysisController.cs       # REST endpoints
│
├── Data/
│   └── MemoryStore.cs              # SQLite persistence (jobs, reports, traces)
│
├── Models/
│   └── Models.cs                   # All domain models and enums
│
├── Services/
│   ├── Orchestrator.cs             # Job lifecycle, per-ticker pipeline
│   ├── OllamaService.cs            # LLM client (Ollama /api/chat)
│   └── ReportWriter.cs             # JSON + Markdown file generation
│
├── Tools/
│   └── FinancialToolkit.cs         # All 10 agent tools + Yahoo Finance calls
│
├── Properties/
│   └── launchSettings.json         # http://localhost:5000
│
├── appsettings.json                # Ollama URL, model, DB path, report dir
└── Program.cs                      # DI registration, middleware, startup banner
```

---

## Configuration

`appsettings.json`:

```json
{
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "Model": "llama3.1:8b"
  },
  "Database": {
    "Path": "data/financial_advisor.db"
  },
  "Reports": {
    "OutputDirectory": "./reports"
  },
  "Urls": "http://localhost:5000"
}
```

To use a different model, change `"Model"` to any model you have pulled in Ollama (e.g. `"mistral"`, `"llama3.2:3b"`).

---

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Ollama](https://ollama.ai) running locally

### 1. Pull the LLM model

```bash
ollama pull llama3.1:8b
```

### 2. Clone and run

```bash
git clone https://github.com/ugvenkat/FinancialAdvisor.git
cd FinancialAdvisor/FinancialAdvisor
dotnet run
```

The API starts at `http://localhost:5000`. Swagger is available at `http://localhost:5000/swagger`.

### 3. Start an analysis

```bash
curl -X POST http://localhost:5000/api/analysis \
  -H "Content-Type: application/json" \
  -d '{"tickers": ["AAPL"]}'
```

Response:
```json
{
  "jobId": "A3F2B1C4",
  "status": "Queued",
  "statusUrl": "http://localhost:5000/api/analysis/A3F2B1C4",
  "liveStatusUrl": "http://localhost:5000/api/analysis/A3F2B1C4/live"
}
```

### 4. Watch it run *(optional)*

```bash
# Poll live agent status every 5 seconds
watch -n 5 curl -s http://localhost:5000/api/analysis/A3F2B1C4/live
```

### 5. Get results

```bash
curl http://localhost:5000/api/analysis/A3F2B1C4
```

---

## Output & Reports

Each completed job writes four files to `./reports/{jobId}/`:

| File | Contents |
|---|---|
| `report.json` | Full structured data — all agent scores, recommendations, raw metrics |
| `report.md` | Human-readable portfolio summary with score cards and key metrics |
| `{TICKER}.md` | Per-ticker report with fundamental, sentiment and risk breakdowns |
| `{TICKER}_agent_traces.md` | Every agent's full Thought → Action → Observation chain |

Example recommendation output:

```
AAPL — BUY  |  78% Confidence  |  Risk: High
Price: $189.30  →  Target: $210.00  (+11.0%)  |  Horizon: 12 months

Fundamental:  Score 72/100  Grade B
Sentiment:    Bullish  +0.42  (60% Bull / 25% Neutral / 15% Bear)
Risk:         High  62/100
```

---

## Dependencies

| Package | Version | Purpose |
|---|---|---|
| `Microsoft.Data.Sqlite` | 8.0.0 | Job and report persistence |
| `Newtonsoft.Json` | 13.0.3 | JSON parsing (LLM responses + API data) |
| `Microsoft.AspNetCore.Mvc.NewtonsoftJson` | 8.0.0 | Controller JSON serialization |
| `HtmlAgilityPack` | 1.11.62 | MarketWatch HTML scraping |
| `Serilog.AspNetCore` | 8.0.1 | Structured logging |
| `Serilog.Sinks.Console` | 5.0.1 | Console log output |
| `Serilog.Sinks.File` | 5.0.0 | Rolling log files (`logs/advisor-YYYYMMDD.log`) |
| `Swashbuckle.AspNetCore` | 6.7.3 | Swagger UI |

---

## Known Limitations

- **MarketWatch scraper is fragile** — the HTML selector may break if MarketWatch changes their page structure. Yahoo Finance JSON endpoints are used for all critical data.
- **Sequential tickers** — tickers in a job are processed one at a time to avoid overloading the local GPU/CPU. Parallel ticker processing can be enabled by switching the `foreach` to `Task.WhenAll` in `Orchestrator.cs`.
- **No job cancellation** — once started, a job runs to completion. A `CancellationTokenSource` registry per job would enable `CancelJobAsync`.
- **No report TTL** — report files in `./reports/` accumulate indefinitely. Add a cleanup job if disk space is a concern.
- **Company name resolver** — `ResolveCompanyName` in `DataCollectionAgent` covers 15 well-known tickers and falls back to `"{TICKER} Corp."` for others.
- **Local LLM only** — the system is built for Ollama. Switching to OpenAI or another provider requires replacing `OllamaService`.
