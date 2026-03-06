using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace WinKVM.Agent;

/// Cloud LLM OCR — sends screenshot to Claude/OpenAI/Grok for vision-based OCR.
/// Higher accuracy for complex UIs; requires internet + API key.
public sealed class CloudLlmOcr : IOcrProvider
{
    public enum CloudApi { Claude, OpenAI, Grok }

    public string Name => $"Cloud LLM OCR ({_api})";

    private readonly CloudApi _api;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public CloudLlmOcr(CloudApi api = CloudApi.Claude) => _api = api;

    public async Task<string> RecognizeAsync(byte[] bgraPixels, int width, int height, CancellationToken ct = default)
    {
        // Convert BGRA to JPEG
        var jpeg = ScreenshotEncoder.EncodeJpeg(bgraPixels, width, height, 0.85f);
        var b64  = Convert.ToBase64String(jpeg);

        return _api switch
        {
            CloudApi.Claude => await AskClaude(b64, ct),
            CloudApi.OpenAI => await AskOpenAI(b64, ct),
            CloudApi.Grok   => await AskGrok(b64, ct),
            _               => throw new NotImplementedException()
        };
    }

    private static async Task<string> AskClaude(string b64, CancellationToken ct)
    {
        var key = AIKeyStore.LoadKey("claude") ?? throw new AIProviderException("No Claude API key");
        var body = new JsonObject
        {
            ["model"]      = "claude-haiku-4-5-20251001",
            ["max_tokens"] = 2048,
            ["messages"]   = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"]       = "base64",
                                ["media_type"] = "image/jpeg",
                                ["data"]       = b64
                            }
                        },
                        new JsonObject { ["type"] = "text", ["text"] = "Extract all visible text from this screen. Return only the text, preserving layout." }
                    }
                }
            }
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            { Content = JsonContent.Create(body) };
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", "2023-06-01");
        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(json)?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
    }

    private static async Task<string> AskOpenAI(string b64, CancellationToken ct)
    {
        var key  = AIKeyStore.LoadKey("openai") ?? throw new AIProviderException("No OpenAI API key");
        var body = new JsonObject
        {
            ["model"]    = "gpt-4o-mini",
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = "Extract all visible text from this screen. Return only the text." },
                        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject
                            { ["url"] = $"data:image/jpeg;base64,{b64}" } }
                    }
                }
            }
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(json)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
    }

    private static Task<string> AskGrok(string b64, CancellationToken ct)
        => AskOpenAI(b64, ct); // Same API shape as OpenAI, different base URL in full impl
}
