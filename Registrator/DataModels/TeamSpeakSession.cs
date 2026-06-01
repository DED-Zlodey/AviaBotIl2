using System;
using System.Collections.Generic;

namespace Registrator.DataModels;

public class TeamSpeakSession
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public int? InitialTsChannelId { get; set; }
    public int? FinalTsChannelId { get; set; }
    public DateTimeOffset ConnectedAt { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }
    public string? DisconnectReason { get; set; }
    public string? IpAddress { get; set; }

    public TeamSpeakUser User { get; set; } = null!;
    public List<ChannelMoveLog>? MoveLogs { get; set; }
}
