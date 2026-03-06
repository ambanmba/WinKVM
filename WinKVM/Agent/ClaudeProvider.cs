using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinKVM.Agent;

public sealed class ClaudeProvider : IAIProvider
{
    public string Name           => "Claude";
    public bool   SupportsVision => true;

    private readonly string _model;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public ClaudeProvider(string model = "claude-sonnet-4-20250514") => _model = model;

    public async Task<AIResponse> SendAgentTurnAsync(
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        byte[]? screenshot,
        CancellationToken ct = default)
    {
        var apiKey = AIKeyStore.LoadKey("claude")
            ?? throw new AIProviderException("No Claude API key configured");

        // Build messages array
        var apiMessages = new JsonArray();
        for (int idx = 0; idx < messages.Count; idx++)
        {
            var msg    = messages[idx];
            bool isLast = idx == messages.Count - 1;
            JsonNode content;

            var imgData = msg.ImageData ?? (isLast && msg.Role == "user" ? screenshot : null);
            if (imgData is not null && isLast)
            {
                content = new JsonArray
                {
                    new JsonObject { ["type"] = "image", ["source"] = new JsonObject
                    {
                        ["type"]       = "base64",
                        ["media_type"] = "image/jpeg",
                        ["data"]       = Convert.ToBase64String(imgData)
                    }},
                    new JsonObject { ["type"] = "text", ["text"] = msg.Content }
                };
            }
            else
            {
                content = JsonValue.Create(msg.Content)!;
            }

            apiMessages.Add(new JsonObject { ["role"] = msg.Role, ["content"] = content });
        }

        var body = new JsonObject
        {
            ["model"]      = _model,
            ["max_tokens"] = 4096,
            ["system"]     = systemPrompt,
            ["messages"]   = apiMessages
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Add("x-api-key",          apiKey);
        req.Headers.Add("anthropic-version",   "2023-06-01");

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new AIProviderException($"Claude API error {(int)resp.StatusCode}: {json}", (int)resp.StatusCode);

        return ParseClaudeResponse(json);
    }

    private static AIResponse ParseClaudeResponse(string json)
    {
        var root    = JsonNode.Parse(json);
        var content = root?["content"]?.AsArray();
        if (content is null) throw new AIProviderException("Invalid Claude response");

        var sb = new System.Text.StringBuilder();
        foreach (var block in content)
        {
            if (block?["type"]?.GetValue<string>() == "text")
                sb.Append(block["text"]?.GetValue<string>());
        }

        return AIResponseParser.Parse(sb.ToString());
    }
}
