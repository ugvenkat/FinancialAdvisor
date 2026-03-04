using FinancialAdvisor.Models;
using FinancialAdvisor.Services;

namespace FinancialAdvisor.Agents;

public class ReActEngine
{
    private readonly IOllamaService      _llm;
    private readonly AgentStatusTracker  _tracker;
    private readonly ILogger<ReActEngine> _log;
    private const int MaxSteps          = 6;
    private static readonly TimeSpan LlmTimeout  = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(30);

    public ReActEngine(IOllamaService llm, AgentStatusTracker tracker, ILogger<ReActEngine> log)
    {
        _llm     = llm;
        _tracker = tracker;
        _log     = log;
    }

    public async Task<AgentTrace> RunAsync(
        string jobId,
        string agentName,
        string ticker,
        string goal,
        List<AgentTool> tools,
        Dictionary<string, Func<string, Task<string>>> toolExecutors,
        CancellationToken ct = default)
    {
        var trace        = new AgentTrace { AgentName = agentName, Ticker = ticker, Goal = goal };
        var systemPrompt = BuildSystemPrompt(agentName, tools);
        var history      = new List<(string role, string content)>();

        history.Add(("user",
            "Your goal: " + goal + "\nTicker: " + ticker +
            "\n\nBegin your reasoning. Use the tools to gather data."));

        _tracker.Update(jobId, ticker, agentName, 0, "Starting...");
        _log.LogInformation("[{Agent}][{Ticker}] START ReAct loop", agentName, ticker);

        for (int step = 1; step <= MaxSteps; step++)
        {
            _tracker.Update(jobId, ticker, agentName, step,
                "Step " + step + " — asking LLM what to do next...");
            _log.LogInformation("[{Agent}][{Ticker}] Step {Step}/{Max} — LLM call",
                agentName, ticker, step, MaxSteps);

            // ── LLM call with timeout ──────────────────────────────────
            string llmResponse;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(LlmTimeout);
                llmResponse = await _llm.CompleteAsync(
                    systemPrompt, BuildHistoryPrompt(history), cts.Token);
            }
            catch (OperationCanceledException)
            {
                _log.LogWarning("[{Agent}][{Ticker}] LLM TIMEOUT at step {Step}",
                    agentName, ticker, step);
                _tracker.Update(jobId, ticker, agentName, step, "TIMEOUT — LLM took too long");
                trace.FinalAnswer = "LLM timed out at step " + step + ". Partial analysis only.";
                trace.Succeeded   = false;
                break;
            }

            _log.LogDebug("[{Agent}][{Ticker}] Step {Step} response ({Len} chars): {Preview}",
                agentName, ticker, step, llmResponse.Length,
                llmResponse[..Math.Min(200, llmResponse.Length)]);

            var parsed        = ParseLLMResponse(llmResponse);
            parsed.StepNumber = step;
            trace.Steps.Add(parsed);
            history.Add(("assistant", llmResponse));

            // ── Final answer ───────────────────────────────────────────
            if (parsed.IsFinal)
            {
                trace.FinalAnswer = parsed.FinalAnswer;
                trace.Succeeded   = true;
                _tracker.Complete(jobId, ticker, agentName, "Done at step " + step);
                _log.LogInformation("[{Agent}][{Ticker}] FINAL ANSWER at step {Step}",
                    agentName, ticker, step);
                break;
            }

            // ── Tool call ──────────────────────────────────────────────
            if (!string.IsNullOrEmpty(parsed.Action))
            {
                var toolName      = parsed.Action.Trim().ToLowerInvariant();
                var safeInput     = SanitizeActionInput(parsed.ActionInput);
                var inputPreview  = safeInput.Length > 80 ? safeInput[..80] : safeInput;

                _tracker.Update(jobId, ticker, agentName, step,
                    "Step " + step + " — tool: " + parsed.Action + "(" + inputPreview + ")");
                _log.LogInformation("[{Agent}][{Ticker}] Step {Step} TOOL: {Tool} Input: {Input}",
                    agentName, ticker, step, parsed.Action, inputPreview);

                if (toolExecutors.TryGetValue(toolName, out var executor))
                {
                    string observation;
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(ToolTimeout);
                        observation = await executor(safeInput).WaitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        observation = "Tool '" + parsed.Action + "' timed out after " +
                                      ToolTimeout.TotalSeconds + "s — no data from this source.";
                        _log.LogWarning("[{Agent}][{Ticker}] TOOL TIMEOUT: {Tool}",
                            agentName, ticker, parsed.Action);
                    }
                    catch (Exception ex)
                    {
                        observation = "Tool '" + parsed.Action + "' error: " + ex.Message;
                        _log.LogWarning(ex, "[{Agent}][{Ticker}] TOOL ERROR: {Tool}",
                            agentName, ticker, parsed.Action);
                    }

                    if (observation.Length > 3000)
                        observation = observation[..3000] + "\n[truncated]";

                    parsed.Observation = observation;
                    history.Add(("user",
                        "Observation: " + observation + "\n\nContinue your reasoning."));

                    _log.LogInformation("[{Agent}][{Ticker}] Step {Step} OBS: {Obs}",
                        agentName, ticker, step,
                        observation[..Math.Min(150, observation.Length)]);
                }
                else
                {
                    var err = "Tool '" + parsed.Action + "' not found. Available: " +
                              string.Join(", ", toolExecutors.Keys);
                    parsed.Observation = err;
                    history.Add(("user",
                        "Observation: " + err + "\n\nUse one of the available tools."));
                    _log.LogWarning("[{Agent}][{Ticker}] UNKNOWN TOOL: {Tool}",
                        agentName, ticker, parsed.Action);
                }
            }
            else if (!parsed.IsFinal)
            {
                history.Add(("user",
                    "Please continue. Call a tool or provide FINAL ANSWER."));
            }
        }

        if (!trace.Succeeded && string.IsNullOrEmpty(trace.FinalAnswer))
        {
            trace.FinalAnswer = "Max steps reached without final answer.";
            _tracker.Complete(jobId, ticker, agentName, "Max steps reached");
            _log.LogWarning("[{Agent}][{Ticker}] Max steps ({Max}) reached",
                agentName, ticker, MaxSteps);
        }

        return trace;
    }

    // ── System Prompt ──────────────────────────────────────────────────

    private static string BuildSystemPrompt(string agentName, List<AgentTool> tools)
    {
        var toolDocs = string.Join("\n\n", tools.Select(t =>
            "  Tool: " + t.Name +
            "\n  Description: " + t.Description +
            "\n  Parameters: " + t.Parameters));

        return
            "You are " + agentName + ", an autonomous AI financial analyst agent.\n" +
            "You operate in a Thought - Action - Observation loop (ReAct pattern).\n\n" +
            "AVAILABLE TOOLS:\n" + toolDocs + "\n\n" +
            "RESPONSE FORMAT - follow this exactly:\n\n" +
            "To use a tool:\n" +
            "Thought: [one sentence of reasoning]\n" +
            "Action: [exact tool name in lowercase]\n" +
            "Action Input: {\"ticker\": \"AAPL\"}\n\n" +
            "When done gathering data:\n" +
            "Thought: [summary of findings]\n" +
            "FINAL ANSWER: [your complete structured answer]\n\n" +
            "CRITICAL RULES:\n" +
            "- Action Input must be pure JSON only - no text after the closing brace\n" +
            "- Always use lowercase key: {\"ticker\": \"AAPL\"} never {\"TICKER\": \"AAPL\"}\n" +
            "- Do NOT write status messages like 'waiting for response'\n" +
            "- Do NOT explain what the tool will do - just output the JSON\n" +
            "- Only ONE action per response\n" +
            "- Never add text after the Action Input JSON";
    }

    private static string BuildHistoryPrompt(List<(string role, string content)> history)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (role, content) in history)
        {
            sb.AppendLine(role == "user" ? "User: " + content : "Agent: " + content);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ── LLM Response Parser ────────────────────────────────────────────

    private static AgentStep ParseLLMResponse(string response)
    {
        var step = new AgentStep();

        var thoughtMatch = System.Text.RegularExpressions.Regex.Match(response,
            @"Thought:\s*(.+?)(?=Action:|FINAL ANSWER:|$)",
            System.Text.RegularExpressions.RegexOptions.Singleline |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (thoughtMatch.Success)
            step.Thought = thoughtMatch.Groups[1].Value.Trim();

        var finalMatch = System.Text.RegularExpressions.Regex.Match(response,
            @"FINAL ANSWER:\s*(.+)$",
            System.Text.RegularExpressions.RegexOptions.Singleline |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (finalMatch.Success)
        {
            step.IsFinal     = true;
            step.FinalAnswer = finalMatch.Groups[1].Value.Trim();
            return step;
        }

        var actionMatch = System.Text.RegularExpressions.Regex.Match(response,
            @"Action:\s*(.+?)(?:\n|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (actionMatch.Success)
            step.Action = actionMatch.Groups[1].Value.Trim();

        var inputMatch = System.Text.RegularExpressions.Regex.Match(response,
            @"Action Input:\s*(.+?)(?=Observation:|Thought:|FINAL ANSWER:|$)",
            System.Text.RegularExpressions.RegexOptions.Singleline |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (inputMatch.Success)
            step.ActionInput = SanitizeActionInput(inputMatch.Groups[1].Value.Trim());

        return step;
    }

    /// <summary>
    /// Strips LLM hallucinated text after the JSON.
    /// The LLM sometimes appends commentary like "WAITING FOR RESPONSE..." after the JSON.
    /// </summary>
    private static string SanitizeActionInput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "{}";

        raw = raw.Trim();

        // If starts with { find matching } and stop there
        if (raw.StartsWith("{"))
        {
            int depth = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '{') depth++;
                else if (raw[i] == '}')
                {
                    depth--;
                    if (depth == 0) return raw[..(i + 1)];
                }
            }
        }

        // If starts with [ find matching ]
        if (raw.StartsWith("["))
        {
            int depth = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '[') depth++;
                else if (raw[i] == ']')
                {
                    depth--;
                    if (depth == 0) return raw[..(i + 1)];
                }
            }
        }

        // Try to find a JSON object anywhere in the string
        var jsonMatch = System.Text.RegularExpressions.Regex.Match(raw, @"\{[^{}]+\}");
        if (jsonMatch.Success) return jsonMatch.Value;

        // Plain ticker string fallback — take first word only
        var plain = raw.Split('\n')[0].Trim().Trim('"').Trim('\'');
        if (plain.Length > 0 && plain.Length <= 10 &&
            plain.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-'))
            return "{\"ticker\": \"" + plain.ToUpper() + "\"}";

        return "{}";
    }
}
