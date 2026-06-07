using System;
using System.Text.Json.Serialization;

namespace Registrator.RabbitMq;

public class Ts3EventEnvelope
{
    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("payload")]
    public object Payload { get; set; } = new();
}
