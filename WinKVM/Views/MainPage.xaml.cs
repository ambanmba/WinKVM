using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WinKVM.Agent;
using WinKVM.Input;
using WinKVM.Models;
using WinKVM.Protocol;

namespace WinKVM.Views;

public sealed partial class MainPage : Page
{
    private readonly ERICSession  _session     = new();
    public  ERICSession           Session      => _session;
    private readonly ProfileStore _profileStore = new();
    private AgentLoop? _agentLoop;
    private DispatcherTimer? _fpsTimer;

    public MainPage()
    {
        InitializeComponent();

        SharpnessSlider.Value = 60; // default CAS sharpness

        LoginPage.SetProfileStore(_profileStore);
        LoginPage.ConnectRequested += (host, port, user, pass) =>
            _session.Connect(host, port, user, pass);

        SendTextFlyout.Session = _session;

        _session.StateChanged += OnSessionStateChanged;
        _session.CertificateChallenge += OnCertificateChallenge;

        // Wire up renderer
        _session.Renderer = KvmRenderer;

        // Keyboard input is handled via Win32 HWND subclassing in MainWindow.
    }

    // ── Session state ─────────────────────────────────────────────────────────

    private void OnSessionStateChanged(SessionState state)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            LoginScroll.Visibility     = state == SessionState.Disconnected ? Visibility.Visible  : Visibility.Collapsed;
            ConnectingPanel.Visibility = (state == SessionState.Connecting || state == SessionState.Authenticating)
                                        ? Visibility.Visible : Visibility.Collapsed;
            ConnectedPanel.Visibility  = state == SessionState.Connected ? Visibility.Visible : Visibility.Collapsed;

            var msg = _session.StatusMessage;
            StatusText.Text       = msg;
            if (state == SessionState.Disconnected && msg.StartsWith("Connection error"))
                StatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
            else
                StatusText.ClearValue(TextBlock.ForegroundProperty);

            DisconnectBtn.IsEnabled  = state == SessionState.Connected;
            CtrlAltDelBtn.IsEnabled  = state == SessionState.Connected;
            ScreenshotBtn.IsEnabled  = state == SessionState.Connected;
            SendTextBtn.IsEnabled    = state == SessionState.Connected;
            PasteBtn.IsEnabled       = state == SessionState.Connected;
            AudioBtn.IsEnabled       = state == SessionState.Connected;
            UpdateAudioBtn();

            if (state == SessionState.Connected)
            {
                ConnectingText.Text = "";
                InitAgentLoop();
                ApplyAspectRatio(); // size renderer to native FB aspect ratio
                FpsText.Visibility = Visibility.Visible;
                _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _fpsTimer.Tick += (_, _) =>
                {
                    FpsText.Text = $"{_session.CurrentFps:F0}/{_session.AvgFps:F0} fps";
                };
                _fpsTimer.Start();
            }
            else if (state == SessionState.Disconnected || state is SessionState)
            {
                _agentLoop?.Stop();
                _agentLoop = null;
                AiPanel.Visibility = Visibility.Collapsed;
                _fpsTimer?.Stop();
                _fpsTimer = null;
                FpsText.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void InitAgentLoop()
    {
        IAIProvider ai = new ClaudeProvider();
        IOcrProvider ocr = new WindowsOcr();
        _agentLoop = new AgentLoop(ai, ocr, _session);
        AiPanel.SetAgentLoop(_agentLoop, _session);
    }

    private async Task<bool> OnCertificateChallenge(string fingerprint, string message)
    {
        var tcs = new TaskCompletionSource<bool>();
        DispatcherQueue.TryEnqueue(async () =>
        {
            CertDialogText.Text  = message;
            CertDialog.XamlRoot  = XamlRoot;
            var result = await CertDialog.ShowAsync();
            tcs.SetResult(result == ContentDialogResult.Primary);
        });
        return await tcs.Task;
    }

    // ── Toolbar actions ───────────────────────────────────────────────────────

    private void DisconnectBtn_Click  (object s, RoutedEventArgs e) => _session.Disconnect();
    private void CtrlAltDelBtn_Click  (object s, RoutedEventArgs e) => _session.SendCtrlAltDel();
    private void ReconnectBtn_Click   (object s, RoutedEventArgs e) => _session.Reconnect();
    private void BackToLoginBtn_Click (object s, RoutedEventArgs e) => _session.Disconnect();
    private void SharpnessSlider_ValueChanged(object s, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => KvmRenderer.Sharpness = (float)(e.NewValue / 100.0);

    // Maintain native framebuffer aspect ratio — letterbox with black bars.
    private void KvmContainer_SizeChanged(object s, SizeChangedEventArgs e)
        => ApplyAspectRatio();

    private void ApplyAspectRatio()
    {
        int fbW = _session.FramebufferWidth;
        int fbH = _session.FramebufferHeight;
        if (fbW <= 0 || fbH <= 0) return;  // not connected yet

        double cW = KvmContainer.ActualWidth;
        double cH = KvmContainer.ActualHeight;
        if (cW <= 0 || cH <= 0) return;

        double fbAspect = (double)fbW / fbH;
        double cAspect  = cW / cH;

        double rendW, rendH;
        if (fbAspect >= cAspect)
        {
            rendW = cW;
            rendH = cW / fbAspect;
        }
        else
        {
            rendH = cH;
            rendW = cH * fbAspect;
        }

        KvmRenderer.Width  = rendW;
        KvmRenderer.Height = rendH;
    }

    private void NpuToggle_Toggled(object s, RoutedEventArgs e)
    {
        KvmRenderer.NpuSharpenEnabled = NpuToggle.IsOn;
        SharpnessSlider.IsEnabled = !NpuToggle.IsOn;  // mutually exclusive
    }

    private void DiagnosticsBtn_Click (object s, RoutedEventArgs e) { /* TODO */ }
    private void SettingsBtn_Click    (object s, RoutedEventArgs e) { /* TODO */ }
    private void SendTextBtn_Click    (object s, RoutedEventArgs e)
    {
        SendTextFlyout.Visibility = SendTextFlyout.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }
    private void AIAgentBtn_Click     (object s, RoutedEventArgs e)
    {
        AiPanel.Visibility = AiPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void AudioBtn_Click(object s, RoutedEventArgs e)
    {
        if (_session.IsAudioActive)
            _session.StopAudio();
        else
            await _session.StartAudioAsync();
        UpdateAudioBtn();
    }

    private void UpdateAudioBtn()
    {
        AudioBtn.Icon  = new SymbolIcon(_session.IsAudioActive ? Symbol.Mute : Symbol.Volume);
        AudioBtn.Label = _session.IsAudioActive ? "Stop Audio" : "Audio";
    }

    private async void ScreenshotBtn_Click(object s, RoutedEventArgs e)
    {
        try
        {
            // CaptureScreenshot uses D3D11 — run on thread pool to avoid UI contention
            var png = await Task.Run(() => KvmRenderer.CaptureScreenshot());
            if (png is null) return;
            var ts   = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "WinKVM", $"WinKVM_{ts}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, png);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Screenshot failed: {ex.Message}";
        }
    }

    private async void PasteBtn_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var text = await GetClipboardTextAsync();
            if (!string.IsNullOrEmpty(text))
                await _session.SendTextAsync(text);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Paste failed: {ex.Message}";
        }
    }

    private static async Task<string?> GetClipboardTextAsync()
    {
        try
        {
            var data = Clipboard.GetContent();
            if (data.Contains(StandardDataFormats.Text))
                return await data.GetTextAsync();
        }
        catch { }
        return null;
    }

    // ── Keyboard input ────────────────────────────────────────────────────────
    // Page-level handlers ensure all keys reach the KVM regardless of focus.

    private void KvmRenderer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_session.State != SessionState.Connected) return;
        if (KeyboardHandler.RaritanKeyCode(e.Key) is { } code)
        {
            _ = _session.SendKeyEventAsync(code, true);
            e.Handled = true;
        }
    }

    private void KvmRenderer_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_session.State != SessionState.Connected) return;
        if (KeyboardHandler.RaritanKeyCode(e.Key) is { } code)
        {
            _ = _session.SendKeyEventAsync(code, false);
            e.Handled = true;
        }
    }

    // ── Mouse input ───────────────────────────────────────────────────────────

    private void KvmRenderer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_session.State != SessionState.Connected) return;
        SendMouseEvent(e); // reads current button state — preserves button during drag
    }

    private void KvmRenderer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session.State != SessionState.Connected) return;
        KvmRenderer.CapturePointer(e.Pointer);
        SendMouseEvent(e);
    }

    private void KvmRenderer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_session.State != SessionState.Connected) return;
        KvmRenderer.ReleasePointerCapture(e.Pointer);
        SendMouseEvent(e);
    }

    private void KvmRenderer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_session.State != SessionState.Connected) return;
        var pt    = e.GetCurrentPoint(KvmRenderer);
        var (x, y) = MouseHandler.FramebufferCoords(pt.Position.X, pt.Position.Y,
            KvmRenderer.ActualWidth, KvmRenderer.ActualHeight,
            _session.FramebufferWidth, _session.FramebufferHeight);
        short z = (short)(pt.Properties.MouseWheelDelta / 120);
        _ = _session.SendScrollEventAsync(x, y, 0, z);
    }

    private void SendMouseEvent(PointerRoutedEventArgs e, byte dragButton = 0xFF)
    {
        var pt   = e.GetCurrentPoint(KvmRenderer);
        var mask = dragButton == 0xFF ? MouseHandler.ButtonMask(pt.Properties) : dragButton;
        var (x, y) = MouseHandler.FramebufferCoords(pt.Position.X, pt.Position.Y,
            KvmRenderer.ActualWidth, KvmRenderer.ActualHeight,
            _session.FramebufferWidth, _session.FramebufferHeight);
        _ = _session.SendPointerEventAsync(x, y, mask);
    }
}
