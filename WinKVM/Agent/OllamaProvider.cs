using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace WinKVM.Agent;

public sealed class OllamaProvider : IAIProvider
{
    public string Name           => "Ollama";
    public bool   SupportsVision { get; }

    private readonly string _model;
    private readonly string _baseUrl;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public OllamaProvider(string model = "llama3.2-vision", string baseUrl = "http://localhost:11434")
    {
        _model       = model;
        _baseUrl      = baseUrl.TrimEnd('/');
        SupportsVision = model.Contains("vision", StringComparison.OrdinalIgnoreCase)
                       || model.Contains("llava",  StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AIResponse> SendAgentTurnAsync(
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        byte[]? screenshot,
        CancellationToken ct = default)
    {
        var apiMessages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt }
        };

        for (int idx = 0; idx < messages.Count; idx++)
        {
            var msg     = messages[idx];
            bool isLast = idx == messages.Count - 1;
            var imgData = msg.ImageData ?? (isLast && msg.Role == "user" ? screenshot : null);

            var msgObj = new JsonObject { ["role"] = msg.Role, ["content"] = msg.Content };
            if (imgData is not null && isLast && SupportsVision)
                msgObj["images"] = new JsonArray { Convert.ToBase64String(imgData) };

            apiMessages.Add(msgObj);
        }

        var body = new JsonObject
        {
            ["model"]    = _model,
            ["messages"] = apiMessages,
            ["stream"]   = false
        };

        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/api/chat", body, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new AIProviderException($"Ollama error {(int)resp.StatusCode}: {json}", (int)resp.StatusCode);

        var text = JsonNode.Parse(json)?["message"]?["content"]?.GetValue<string>() ?? "";
        return AIResponseParser.Parse(text);
    }
}
