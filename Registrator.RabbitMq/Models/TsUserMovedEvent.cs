using System.Text.Json.Serialization;

namespace Registrator.RabbitMq;

public class TsUserMovedEvent
{
    [JsonPropertyName("clientId")]
    public int ClientId { get; set; }

    [JsonPropertyName("uniqueId")]
    public string UniqueId { get; set; } = string.Empty;

    [JsonPropertyName("nickname")]
    public string Nickname { get; set; } = string.Empty;

    [JsonPropertyName("fromChannelId")]
    public int? FromChannelId { get; set; }

    [JsonPropertyName("toChannelId")]
    public int ToChannelId { get; set; }

    [JsonPropertyName("movedByUid")]
    public string? MovedByUid { get; set; }
}
