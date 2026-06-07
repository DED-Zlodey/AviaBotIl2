using System.Text.Json.Serialization;

namespace Registrator.RabbitMq;

public class TsUserConnectedEvent
{
    [JsonPropertyName("clientId")]
    public int ClientId { get; set; }

    [JsonPropertyName("uniqueId")]
    public string UniqueId { get; set; } = string.Empty;

    [JsonPropertyName("nickname")]
    public string Nickname { get; set; } = string.Empty;

    [JsonPropertyName("channelId")]
    public int ChannelId { get; set; }

    [JsonPropertyName("countryCode")]
    public string? CountryCode { get; set; }

    [JsonPropertyName("isInputMuted")]
    public bool IsInputMuted { get; set; }

    [JsonPropertyName("isOutputMuted")]
    public bool IsOutputMuted { get; set; }
}
