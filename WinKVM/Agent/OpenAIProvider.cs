using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace WinKVM.Agent;

public sealed class OpenAIProvider : IAIProvider
{
    public string Name           => "OpenAI";
    public bool   SupportsVision => true;

    private readonly string _model;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public OpenAIProvider(string model = "gpt-4o") => _model = model;

    public async Task<AIResponse> SendAgentTurnAsync(
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        byte[]? screenshot,
        CancellationToken ct = default)
    {
        var apiKey = AIKeyStore.LoadKey("openai")
            ?? throw new AIProviderException("No OpenAI API key configured");

        var apiMessages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt }
        };

        for (int idx = 0; idx < messages.Count; idx++)
        {
            var msg     = messages[idx];
            bool isLast = idx == messages.Count - 1;
            var imgData = msg.ImageData ?? (isLast && msg.Role == "user" ? screenshot : null);

            JsonNode content;
            if (imgData is not null && isLast)
            {
                content = new JsonArray
                {
                    new JsonObject { ["type"] = "text",      ["text"] = msg.Content },
                    new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject
                    {
                        ["url"] = $"data:image/jpeg;base64,{Convert.ToBase64String(imgData)}"
                    }}
                };
            }
            else content = JsonValue.Create(msg.Content)!;

            apiMessages.Add(new JsonObject { ["role"] = msg.Role, ["content"] = content });
        }

        var body = new JsonObject
        {
            ["model"]       = _model,
            ["max_tokens"]  = 4096,
            ["messages"]    = apiMessages
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new AIProviderException($"OpenAI API error {(int)resp.StatusCode}: {json}", (int)resp.StatusCode);

        var text = JsonNode.Parse(json)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        return AIResponseParser.Parse(text);
    }
}
