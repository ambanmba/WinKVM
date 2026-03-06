using Windows.Storage;

namespace WinKVM.Agent;

/// Stores AI provider API keys in Windows ApplicationData LocalSettings.
/// Keys are identified by provider name (e.g. "claude", "openai", "grok").
public static class AIKeyStore
{
    private const string Prefix = "ai_key_";

    public static string? LoadKey(string provider)
    {
        var key = Prefix + provider.ToLowerInvariant();
        return ApplicationData.Current.LocalSettings.Values[key] as string;
    }

    public static void SaveKey(string provider, string apiKey)
    {
        var key = Prefix + provider.ToLowerInvariant();
        ApplicationData.Current.LocalSettings.Values[key] = apiKey;
    }

    public static void DeleteKey(string provider)
    {
        var key = Prefix + provider.ToLowerInvariant();
        ApplicationData.Current.LocalSettings.Values.Remove(key);
    }
}
