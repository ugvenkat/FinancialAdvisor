using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FinancialAdvisor.Services;

public interface IOllamaService
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
    Task<T?> CompleteJsonAsync<T>(string systemPrompt, string userPrompt, CancellationToken ct = default);
}

public class OllamaService : IOllamaService
{
    private readonly HttpClient _http;
    private readonly string     _model;
    private readonly ILogger<OllamaService> _log;

    public OllamaService(HttpClient http, IConfiguration cfg, ILogger<OllamaService> log)
    {
        _http  = http;
        _model = cfg["Ollama:Model"] ?? "llama3.2";
        _log   = log;
    }

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
    {
        var payload = new
        {
            model    = _model,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user",   content = user   }
            },
            stream  = false,
            options = new { temperature = 0.1, num_predict = 2048 }
        };

        try
        {
            var resp = await _http.PostAsync("/api/chat",
                new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json"), ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct);
            return JObject.Parse(body)["message"]?["content"]?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ollama call failed");
            return $"[LLM_ERROR: {ex.Message}]";
        }
    }

    public async Task<T?> CompleteJsonAsync<T>(string system, string user, CancellationToken ct = default)
    {
        var raw = await CompleteAsync(
            system + "\n\nRESPOND WITH VALID JSON ONLY. No markdown fences, no explanation.", user, ct);

        raw = raw.Trim();
        if (raw.StartsWith("```")) raw = raw[(raw.IndexOf('\n') + 1)..];
        if (raw.EndsWith("```"))   raw = raw[..raw.LastIndexOf("```")];
        raw = raw.Trim();

        try   { return JsonConvert.DeserializeObject<T>(raw); }
        catch { return default; }
    }
}
