using System.Text.Json.Serialization;

namespace RouteScope.Core.Models;

/// <summary>A saved subscription URL. Servers fetched from it are tagged with its id.</summary>
public sealed class Subscription : NotifyBase
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime? LastUpdatedUtc { get; set; }
    public int NodeCount { get; set; }
    public string? LastError { get; set; }

    [JsonIgnore] public string LastUpdatedLabel => LastUpdatedUtc is null
        ? "never updated"
        : $"updated {LastUpdatedUtc.Value.ToLocalTime():dd MMM HH:mm}";

    [JsonIgnore] public string CountLabel => NodeCount == 1 ? "1 server" : $"{NodeCount} servers";
}
