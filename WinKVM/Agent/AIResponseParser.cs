using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WinKVM.Agent;

/// Parses AI provider text responses into AIResponse objects.
/// Handles well-formed JSON, markdown-wrapped JSON, and malformed outputs from small models.
public static class AIResponseParser
{
    public static AIResponse Parse(string text)
    {
        var trimmed = text.Trim();

        // Extract JSON from possible markdown code block
        var jsonStr = ExtractJson(trimmed);
        if (jsonStr is null)
            return new AIResponse(null, [], trimmed);

        // Repair unbalanced braces/brackets
        jsonStr = RepairJson(jsonStr);

        try
        {
            var node = JsonNode.Parse(jsonStr);
            if (node is JsonObject obj)
            {
                var thinking = obj["thinking"]?.GetValue<string>();
                var message  = obj["message"]?.GetValue<string>();
                var actions  = ParseActions(obj["actions"]);

                if (actions.Count > 0 || thinking is not null || message is not null)
                    return new AIResponse(thinking, actions, message);

                // Try as single action
                if (TrySingleAction(obj) is { } single)
                    return new AIResponse(null, [single], null);
            }
        }
        catch { /* fall through to regex extraction */ }

        // Last resort: extract actions via regex
        var extracted = ExtractActionsFromText(jsonStr ?? trimmed);
        return extracted;
    }

    private static string? ExtractJson(string text)
    {
        // Strip markdown code fence
        var fence = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)```");
        if (fence.Success) return fence.Groups[1].Value.Trim();

        // Find outermost { ... }
        int start = text.IndexOf('{');
        int end   = text.LastIndexOf('}');
        if (start >= 0 && end > start) return text[start..(end + 1)];

        return null;
    }

    private static string RepairJson(string json)
    {
        int braces = 0, brackets = 0;
        bool inString = false, escaped = false;
        foreach (char ch in json)
        {
            if (escaped) { escaped = false; continue; }
            if (ch == '\\' && inString) { escaped = true; continue; }
            if (ch == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (ch == '{') braces++;   else if (ch == '}') braces--;
            if (ch == '[') brackets++; else if (ch == ']') brackets--;
        }
        if (brackets > 0) json += new string(']', brackets);
        if (braces   > 0) json += new string('}', braces);
        return json;
    }

    private static List<AIAction> ParseActions(JsonNode? node)
    {
        if (node is null) return [];
        var list = new List<AIAction>();

        if (node is JsonArray arr)
        {
            foreach (var item in arr)
                if (item is JsonObject obj && TrySingleAction(obj) is { } a)
                    list.Add(a);
        }
        return list;
    }

    private static AIAction? TrySingleAction(JsonObject obj)
    {
        var actionName = obj["action"]?.GetValue<string>() ?? obj["type"]?.GetValue<string>();

        if (actionName is null)
        {
            // Key-as-name pattern: {"click": {...}}
            foreach (var name in new[] { "click","double_click","type","key_combo","scroll","move_mouse","wait","screenshot","done" })
                if (obj[name] is not null) { actionName = name; break; }
        }

        return actionName switch
        {
            "click"        => new ClickAction(GetInt(obj,"x"), GetInt(obj,"y"), obj["button"]?.GetValue<string>() ?? "left"),
            "double_click" => new DoubleClickAction(GetInt(obj,"x"), GetInt(obj,"y")),
            "type"         => new TypeAction(obj["text"]?.GetValue<string>() ?? ""),
            "key_combo"    => new KeyComboAction(obj["keys"]?.AsArray().Select(k => k?.GetValue<string>() ?? "").ToArray() ?? []),
            "scroll"       => new ScrollAction(GetInt(obj,"x"), GetInt(obj,"y"), obj["direction"]?.GetValue<string>() ?? "down", GetInt(obj,"amount", 3)),
            "move_mouse"   => new MoveMouseAction(GetInt(obj,"x"), GetInt(obj,"y")),
            "wait"         => new WaitAction(GetDouble(obj,"seconds", 1)),
            "screenshot"   => new ScreenshotAction(),
            "done"         => new DoneAction(obj["summary"]?.GetValue<string>() ?? "Done"),
            _              => null
        };
    }

    private static int    GetInt   (JsonObject o, string k, int    def = 0)  => (int)(o[k]?.GetValue<double>() ?? def);
    private static double GetDouble(JsonObject o, string k, double def = 0d) => o[k]?.GetValue<double>() ?? def;

    private static AIResponse ExtractActionsFromText(string text)
    {
        var thinking = ExtractString("thinking", text);
        var message  = ExtractString("message",  text);
        var actions  = new List<AIAction>();

        // Find balanced {...} objects and try parsing each as an action
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] != '{') { i++; continue; }
            int depth = 0, j = i;
            bool inStr = false, esc = false;
            while (j < text.Length)
            {
                char ch = text[j];
                if (esc) { esc = false; j++; continue; }
                if (ch == '\\' && inStr) { esc = true; j++; continue; }
                if (ch == '"') { inStr = !inStr; j++; continue; }
                if (!inStr) { if (ch == '{') depth++; else if (ch == '}' && --depth == 0) break; }
                j++;
            }
            if (depth == 0 && j < text.Length)
            {
                var sub = text[i..(j+1)];
                try
                {
                    if (JsonNode.Parse(sub) is JsonObject obj &&
                        obj["actions"] is null && obj["thinking"] is null &&
                        TrySingleAction(obj) is { } a)
                    {
                        actions.Add(a);
                        i = j + 1;
                        continue;
                    }
                }
                catch { }
            }
            i++;
        }

        return new AIResponse(thinking, actions, message);
    }

    private static string? ExtractString(string key, string text)
    {
        var m = Regex.Match(text, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }
}
