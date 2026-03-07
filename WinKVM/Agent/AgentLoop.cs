using System.Collections.ObjectModel;
using WinKVM.Protocol;

namespace WinKVM.Agent;

public enum AgentState { Idle, Running, Paused, WaitingForApproval }

/// Drives the AI agent: screenshot → OCR → AI → actions → repeat.
/// Direct port of AgentLoop.swift.
public sealed class AgentLoop
{
    public AgentState State   { get; private set; } = AgentState.Idle;
    public string CurrentAction { get; private set; } = "";

    public ObservableCollection<AgentMessage> Messages { get; } = [];

    public IAIProvider  Provider    { get; set; }
    public IOcrProvider OcrProvider { get; set; }
    public bool RequireApproval     { get; set; }
    public double LoopIntervalSeconds { get; set; } = 2.0;
    public double ScreenshotQuality   { get; set; } = 0.6;

    public event Action? StateChanged;

    private ERICSession? _session;
    private CancellationTokenSource? _cts;
    private int _maxTurns => Provider.SupportsVision ? 15 : 3;
    private int _turnCount;
    private bool _forceScreenshot;
    private string? _lastActionSig;
    private int _consecutiveEmpty, _consecutiveRepeat;

    private TaskCompletionSource? _approvalTcs;

    public AgentLoop(IAIProvider provider, IOcrProvider ocrProvider, ERICSession session)
    {
        Provider    = provider;
        OcrProvider = ocrProvider;
        _session    = session;
    }

    // ── Control ──────────────────────────────────────────────────────────────

    public void Start(string task)
    {
        if (State != AgentState.Idle) return;
        Messages.Clear();
        Messages.Add(new AgentMessage("user", task));
        _turnCount = 0; _forceScreenshot = true;
        _consecutiveEmpty = _consecutiveRepeat = 0;
        _lastActionSig = null;
        SetState(AgentState.Running);
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _approvalTcs?.TrySetResult();
        SetState(AgentState.Idle);
        CurrentAction = "";
    }

    public void Approve()
    {
        if (State != AgentState.WaitingForApproval) return;
        SetState(AgentState.Running);
        _approvalTcs?.TrySetResult();
    }

    public void Pause()  { if (State == AgentState.Running) SetState(AgentState.Paused); }
    public void Resume() { if (State == AgentState.Paused)  { SetState(AgentState.Running); _ = RunLoopAsync(_cts!.Token); } }

    public void Chat(string message)
    {
        if (State != AgentState.Idle) return;
        Messages.Add(new AgentMessage("user", message));
        _forceScreenshot = true;
        SetState(AgentState.Running);
        _cts = new CancellationTokenSource();
        _ = RunSingleTurnAsync(_cts.Token).ContinueWith(_ => { SetState(AgentState.Idle); CurrentAction = ""; });
    }

    // ── Loop ─────────────────────────────────────────────────────────────────

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && State == AgentState.Running)
        {
            _turnCount++;
            if (_turnCount > _maxTurns)
            {
                Messages.Add(new AgentMessage("assistant", $"Stopped: reached maximum {_maxTurns} turns."));
                SetState(AgentState.Idle);
                CurrentAction = "Stopped: max turns reached";
                return;
            }

            await RunSingleTurnAsync(ct);
            if (State != AgentState.Running) return;

            if (Messages.LastOrDefault()?.Content.Contains("\"done\"") == true)
            {
                SetState(AgentState.Idle);
                CurrentAction = "";
                return;
            }

            if (RequireApproval)
            {
                SetState(AgentState.WaitingForApproval);
                CurrentAction = "Waiting for approval...";
                _approvalTcs  = new TaskCompletionSource();
                await _approvalTcs.Task;
                if (ct.IsCancellationRequested || State != AgentState.Running) return;
            }

            await Task.Delay((int)(LoopIntervalSeconds * 1000), ct);
        }
    }

    private async Task RunSingleTurnAsync(CancellationToken ct)
    {
        if (_session is null) return;

        CurrentAction = "Capturing screen...";
        var screenshot = _session.TakeScreenshot();
        if (screenshot is null) { CurrentAction = "No framebuffer available"; return; }

        CurrentAction = "Analysing screen (OCR)...";
        var (w, h) = (_session.FramebufferWidth, _session.FramebufferHeight);
        var analysis = await ScreenAnalyzer.AnalyzeAsync(screenshot, w, h,
            Provider.SupportsVision ? new WindowsOcr() : OcrProvider, ct);

        bool useImage = Provider.SupportsVision || _forceScreenshot;
        _forceScreenshot = false;

        byte[]? imgData = null;
        if (useImage)
        {
            float quality = Provider.SupportsVision ? 0.85f : (float)ScreenshotQuality;
            int maxW = Provider.SupportsVision ? 0 : 1920;
            imgData = await Task.Run(() => ScreenshotEncoder.EncodeJpeg(screenshot, w, h, quality, maxW), ct);
        }

        var observation = $"Screen OCR:\n{analysis.Description}";
        if (!useImage) observation += "\n\n(Text-only mode — request screenshot action if you need visual context)";

        var userMsg = new AgentMessage("user", observation, imgData);
        if (Messages.Count > 10)
        {
            var initial = Messages[0];
            var recent  = Messages.TakeLast(9).ToList();
            Messages.Clear();
            Messages.Add(initial);
            foreach (var m in recent) Messages.Add(m);
        }
        Messages.Add(userMsg);

        CurrentAction = "Thinking...";
        try
        {
            var response = await Provider.SendAgentTurnAsync(SystemPrompt(w, h), Messages, imgData, ct);

            var text = "";
            if (response.Thinking is { } t) text += $"[Thinking: {t}]\n";
            if (response.Message  is { } m) text += m;
            if (text.Length == 0) text = response.Actions.Count == 0 ? "(no response)" : "(executing actions)";
            Messages.Add(new AgentMessage("assistant", text));

            bool hasInteractive = response.Actions.Any(a => a is ClickAction or DoubleClickAction or TypeAction or KeyComboAction or ScrollAction or MoveMouseAction);
            if (!hasInteractive && response.Message is { Length: > 0 } && response.Actions.Count > 0)
            {
                SetState(AgentState.Idle); CurrentAction = ""; return;
            }

            if (response.Actions.Count == 0)
            {
                _consecutiveEmpty++;
                if (_consecutiveEmpty >= 3)
                    Messages.Add(new AgentMessage("user", $"You have not taken any actions for {_consecutiveEmpty} turns. Take an action now."));
            }
            else _consecutiveEmpty = 0;

            var sig = CoarseSignature(response.Actions.FirstOrDefault());
            if (!string.IsNullOrEmpty(sig) && sig != "done" && sig == _lastActionSig)
            {
                if (++_consecutiveRepeat >= 2)
                {
                    Messages.Add(new AgentMessage("assistant", $"Stopped: repeating action ({sig}). Trying different approach."));
                    SetState(AgentState.Idle); CurrentAction = "Stopped: stuck in loop"; return;
                }
            }
            else _consecutiveRepeat = 0;
            _lastActionSig = sig;

            bool lastWasInput = false;
            for (int i = 0; i < response.Actions.Count; i++)
            {
                if (ct.IsCancellationRequested) return;
                var action = response.Actions[i];
                CurrentAction = $"Action {i+1}/{response.Actions.Count}: {DescribeAction(action)}";

                if (action is DoneAction done)
                {
                    CurrentAction = done.Summary;
                    Messages.Add(new AgentMessage("assistant", $"Done: {done.Summary}"));
                    SetState(AgentState.Idle); return;
                }
                if (action is ScreenshotAction) { _forceScreenshot = true; continue; }

                lastWasInput = action is ClickAction or DoubleClickAction or TypeAction or KeyComboAction or ScrollAction or MoveMouseAction;
                await ActionExecutor.ExecuteAsync(action, _session, ct);
            }
            if (lastWasInput) await Task.Delay(300, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            CurrentAction = $"Error: {ex.Message}";
            Messages.Add(new AgentMessage("assistant", $"Error: {ex.Message}"));
        }
    }

    private void SetState(AgentState s) { State = s; StateChanged?.Invoke(); }

    private static string SystemPrompt(int w, int h) => $$"""
        You are an AI agent controlling a remote computer through a KVM device.
        You see the screen through OCR text and optional screenshots.

        SCREEN FORMAT:
        - Screen divided into TOP BAR, MAIN, and BOTTOM BAR regions
        - Elements from OCR text with approximate positions
        - Coordinates are framebuffer pixels. Resolution: {{w}}x{{h}}

        GUI INTERACTION:
        - To click a button: use coordinates near the text centre
        - To fill a text field: click the field first, then use "type"
        - After ANY click or type, add a wait (1-2s) then screenshot to verify

        Respond with JSON: { "thinking": "...", "actions": [...], "message": "..." }

        Available actions:
          {"action":"click","x":500,"y":300,"button":"left"}
          {"action":"double_click","x":500,"y":300}
          {"action":"type","text":"hello"}
          {"action":"key_combo","keys":["ctrl","c"]}
          {"action":"scroll","x":500,"y":300,"direction":"down","amount":3}
          {"action":"move_mouse","x":500,"y":300}
          {"action":"wait","seconds":2}
          {"action":"screenshot"}
          {"action":"done","summary":"Task complete"}
        """;

    private static string DescribeAction(AIAction a) => a switch
    {
        ClickAction(var x, var y, var b)       => $"Click {b} at ({x},{y})",
        DoubleClickAction(var x, var y)         => $"Double-click at ({x},{y})",
        TypeAction(var t)                        => $"Type \"{t[..Math.Min(30, t.Length)]}\"",
        KeyComboAction(var keys)                 => $"Key combo: {string.Join("+", keys)}",
        ScrollAction(_, _, var d, var amt)       => $"Scroll {d} {amt}",
        MoveMouseAction(var x, var y)            => $"Move mouse to ({x},{y})",
        WaitAction(var s)                        => $"Wait {s}s",
        ScreenshotAction                         => "Screenshot",
        DoneAction(var s)                        => $"Done: {s}",
        _                                        => "Unknown"
    };

    private static string CoarseSignature(AIAction? a)
    {
        static int R(int v) => (v / 200) * 200;
        return a switch
        {
            ClickAction(var x, var y, var b)    => $"click {b} ~({R(x)},{R(y)})",
            DoubleClickAction(var x, var y)      => $"dblclick ~({R(x)},{R(y)})",
            TypeAction(var t)                     => $"type {t[..Math.Min(20, t.Length)]}",
            KeyComboAction(var keys)              => $"key {string.Join("+", keys)}",
            ScrollAction(_, _, var d, _)          => $"scroll {d}",
            MoveMouseAction(var x, var y)         => $"move ~({R(x)},{R(y)})",
            WaitAction                            => "wait",
            ScreenshotAction                      => "screenshot",
            DoneAction                            => "done",
            _                                     => ""
        };
    }
}
