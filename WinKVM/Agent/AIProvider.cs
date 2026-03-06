using System.Text.Json.Serialization;

namespace WinKVM.Agent;

// ── AI provider interface ───────────────────────────────────────────────────

public interface IAIProvider
{
    string Name           { get; }
    bool   SupportsVision { get; }

    Task<AIResponse> SendAgentTurnAsync(
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        byte[]? screenshot,
        CancellationToken ct = default);
}

// ── Message types ───────────────────────────────────────────────────────────

public record AgentMessage(string Role, string Content, byte[]? ImageData = null);

public record AIResponse(string? Thinking, IReadOnlyList<AIAction> Actions, string? Message);

// ── Action discriminated union ──────────────────────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "action")]
[JsonDerivedType(typeof(ClickAction),       "click")]
[JsonDerivedType(typeof(DoubleClickAction), "double_click")]
[JsonDerivedType(typeof(TypeAction),        "type")]
[JsonDerivedType(typeof(KeyComboAction),    "key_combo")]
[JsonDerivedType(typeof(ScrollAction),      "scroll")]
[JsonDerivedType(typeof(MoveMouseAction),   "move_mouse")]
[JsonDerivedType(typeof(WaitAction),        "wait")]
[JsonDerivedType(typeof(ScreenshotAction),  "screenshot")]
[JsonDerivedType(typeof(DoneAction),        "done")]
public abstract record AIAction;

public record ClickAction      (int X, int Y, string Button = "left") : AIAction;
public record DoubleClickAction(int X, int Y)                         : AIAction;
public record TypeAction       (string Text)                          : AIAction;
public record KeyComboAction   (string[] Keys)                        : AIAction;
public record ScrollAction     (int X, int Y, string Direction, int Amount) : AIAction;
public record MoveMouseAction  (int X, int Y)                         : AIAction;
public record WaitAction       (double Seconds)                       : AIAction;
public record ScreenshotAction                                        : AIAction;
public record DoneAction       (string Summary)                       : AIAction;

// ── Errors ──────────────────────────────────────────────────────────────────

public class AIProviderException : Exception
{
    public int? StatusCode { get; }
    public AIProviderException(string message, int? statusCode = null) : base(message) => StatusCode = statusCode;
}
