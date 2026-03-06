using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinKVM.Agent;
using WinKVM.Protocol;

namespace WinKVM.Views;

public sealed partial class AIChatPanel : UserControl
{
    private AgentLoop?   _loop;
    private ERICSession? _session;

    public AIChatPanel() => InitializeComponent();

    public void SetAgentLoop(AgentLoop loop, ERICSession session)
    {
        _loop    = loop;
        _session = session;

        loop.Messages.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshMessages);
        loop.StateChanged += () => DispatcherQueue.TryEnqueue(RefreshStatus);
    }

    private void RefreshMessages()
    {
        MessageList.ItemsSource = _loop?.Messages.ToList();
        MessageScroll.ScrollToVerticalOffset(MessageScroll.ExtentHeight);
    }

    private void RefreshStatus()
    {
        AgentStatus.Text = _loop?.CurrentAction ?? "";
    }

    private void RunBtn_Click(object s, RoutedEventArgs e)
    {
        var task = TaskInput.Text.Trim();
        if (string.IsNullOrEmpty(task) || _loop is null) return;
        _loop.Start(task);
        TaskInput.Text = "";
    }

    private void ChatBtn_Click(object s, RoutedEventArgs e)
    {
        var msg = TaskInput.Text.Trim();
        if (string.IsNullOrEmpty(msg) || _loop is null) return;
        _loop.Chat(msg);
        TaskInput.Text = "";
    }

    private void StopBtn_Click(object s, RoutedEventArgs e) => _loop?.Stop();

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loop is null) return;
        _loop.Provider = ProviderCombo.SelectedIndex switch
        {
            1 => new OpenAIProvider(),
            2 => new GrokProvider(),
            3 => new OllamaProvider(),
            _ => new ClaudeProvider()
        };
    }
}
