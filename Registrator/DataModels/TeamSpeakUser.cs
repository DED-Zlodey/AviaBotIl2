using System;
using System.Collections.Generic;

namespace Registrator.DataModels;

public class TeamSpeakUser
{
    public int Id { get; set; }
    public string TsUniqueId { get; set; } = string.Empty;
    public string? Nickname { get; set; }
    public int? CurrentClientId { get; set; }
    public int? CurrentTsChannelId { get; set; }
    public bool IsOnline { get; set; }
    public DateTimeOffset? LastConnectedAt { get; set; }
    public DateTimeOffset? LastDisconnectedAt { get; set; }
    public int TotalConnections { get; set; }
    public string? Country { get; set; }
    public string? Platform { get; set; }
    public string? Version { get; set; }
    public bool IsInputMuted { get; set; }
    public bool IsOutputMuted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<TeamSpeakSession>? Sessions { get; set; }
}
