using System;
using System.Collections.Generic;

namespace Registrator.DataModels;

public class TeamSpeakChannel
{
    public int Id { get; set; }
    public int TsChannelId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? ParentTsChannelId { get; set; }
    public int? Order { get; set; }
    public string? Topic { get; set; }
    public string? Description { get; set; }
    public bool IsPermanent { get; set; }
    public bool IsSemiPermanent { get; set; }
    public bool IsPasswordProtected { get; set; }
    public int? MaxClients { get; set; }
    public int CurrentClientCount { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation properties removed: TS3 sends channels in arbitrary order,
    // so a self-referencing FK constraint would fail when a child arrives before its parent.
    // Use ParentTsChannelId only for tree reconstruction in memory.
}
