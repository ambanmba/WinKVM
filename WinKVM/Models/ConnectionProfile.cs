using System.Text.Json.Serialization;

namespace WinKVM.Models;

public sealed class ConnectionProfile
{
    public Guid   Id           { get; set; } = Guid.NewGuid();
    public string Name         { get; set; } = "";
    public string Host         { get; set; } = "";
    public ushort Port         { get; set; } = 443;
    public string Username     { get; set; } = "";
    public bool   SavePassword { get; set; } = true;
}
