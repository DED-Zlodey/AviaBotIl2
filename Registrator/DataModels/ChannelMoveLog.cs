using System;

namespace Registrator.DataModels;

public class ChannelMoveLog
{
    public int Id { get; set; }
    public int? SessionId { get; set; }
    public int UserId { get; set; }
    public int? FromTsChannelId { get; set; }
    public int ToTsChannelId { get; set; }
    public string? MovedByUid { get; set; }
    public DateTimeOffset MovedAt { get; set; }

    public TeamSpeakUser User { get; set; } = null!;
    public TeamSpeakSession? Session { get; set; }
}
