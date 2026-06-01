using Microsoft.EntityFrameworkCore;
using Registrator.DataModels;

namespace Registrator.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Gamer> Gamers => Set<Gamer>();
    public DbSet<TeamSpeakChannel> TeamSpeakChannels => Set<TeamSpeakChannel>();
    public DbSet<TeamSpeakUser> TeamSpeakUsers => Set<TeamSpeakUser>();
    public DbSet<TeamSpeakSession> TeamSpeakSessions => Set<TeamSpeakSession>();
    public DbSet<ChannelMoveLog> ChannelMoveLogs => Set<ChannelMoveLog>();
    public DbSet<UserAudioStateLog> UserAudioStateLogs => Set<UserAudioStateLog>();
}
