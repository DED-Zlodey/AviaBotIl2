using System.Text.Json.Serialization;

namespace Registrator.RabbitMq;

public class TsUserDisconnectedEvent
{
    [JsonPropertyName("clientId")]
    public int ClientId { get; set; }

    [JsonPropertyName("uniqueId")]
    public string UniqueId { get; set; } = string.Empty;

    [JsonPropertyName("nickname")]
    public string Nickname { get; set; } = string.Empty;

    [JsonPropertyName("channelId")]
    public int? ChannelId { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
