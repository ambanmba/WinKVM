using WinKVM.Protocol;

namespace WinKVM.Agent;

/// Executes AI agent actions against the active ERICSession.
public static class ActionExecutor
{
    public static async Task ExecuteAsync(AIAction action, ERICSession session, CancellationToken ct = default)
    {
        switch (action)
        {
            case ClickAction(var x, var y, var button):
                byte mask = button == "right" ? (byte)4 : button == "middle" ? (byte)2 : (byte)1;
                await session.SendPointerEventAsync((ushort)x, (ushort)y, mask, ct);
                await Task.Delay(50, ct);
                await session.SendPointerEventAsync((ushort)x, (ushort)y, 0, ct);
                break;

            case DoubleClickAction(var x, var y):
                for (int i = 0; i < 2; i++)
                {
                    await session.SendPointerEventAsync((ushort)x, (ushort)y, 1, ct);
                    await Task.Delay(50, ct);
                    await session.SendPointerEventAsync((ushort)x, (ushort)y, 0, ct);
                    if (i == 0) await Task.Delay(100, ct);
                }
                break;

            case TypeAction(var text):
                await session.SendTextAsync(text, ct: ct);
                break;

            case KeyComboAction(var keys):
                var codes = keys.Select(k => KeycodeMapper.KeyCode(k)).Where(c => c.HasValue).Select(c => c!.Value).ToList();
                foreach (var code in codes)
                {
                    await session.SendKeyEventAsync(code, true, ct);
                    await Task.Delay(20, ct);
                }
                foreach (var code in Enumerable.Reverse(codes))
                {
                    await session.SendKeyEventAsync(code, false, ct);
                    await Task.Delay(20, ct);
                }
                break;

            case ScrollAction(var x, var y, var dir, var amount):
                short z = dir == "up" ? (short)amount : (short)(-amount);
                await session.SendScrollEventAsync((ushort)x, (ushort)y, 0, z, ct);
                break;

            case MoveMouseAction(var x, var y):
                await session.SendPointerEventAsync((ushort)x, (ushort)y, 0, ct);
                break;

            case WaitAction(var seconds):
                await Task.Delay((int)(seconds * 1000), ct);
                break;

            case ScreenshotAction:
            case DoneAction:
                // Handled by AgentLoop
                break;
        }
    }
}
