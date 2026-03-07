using WinKVM.Agent;
using WinKVM.Protocol;

namespace WinKVM.Input;

/// Sends a string to the remote by simulating key-down/key-up events per character.
/// Matches the behaviour of TextSender.swift.
public sealed class TextSender
{
    private readonly ERICSession _session;

    public TextSender(ERICSession session) => _session = session;

    public async Task SendAsync(string text, IProgress<(int sent, int total)>? progress = null, CancellationToken ct = default)
    {
        int total = text.Length;
        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            char ch = text[i];
            if (KeycodeMapper.KeyCodesForChar(ch) is { } codes)
            {
                foreach (var (key, shift) in codes)
                {
                    if (shift) await _session.SendKeyEventAsync(KeycodeMapper.LeftShift, true, ct);
                    await _session.SendKeyEventAsync(key, true, ct);
                    await Task.Delay(15, ct);
                    await _session.SendKeyEventAsync(key, false, ct);
                    if (shift) await _session.SendKeyEventAsync(KeycodeMapper.LeftShift, false, ct);
                    await Task.Delay(5, ct);
                }
            }
            progress?.Report((i + 1, total));
        }
    }
}
