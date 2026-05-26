using Microsoft.EntityFrameworkCore;
using Registrator.DataModels;

namespace Registrator.Data;

public class AppDbContext : DbContext
{
	/// <summary>
	/// Класс контекста базы данных приложения, наследующийся от DbContext.
	/// Используется для работы с базой данных, содержащей информацию о "Gamer".
	/// </summary>
	public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

	/// Свойство, представляющее коллекцию сущностей типа Gamer в базе данных.
	/// Используется для выполнения запросов и операций над данными таблицы "Gamers",
	/// которая хранит информацию о геймерах, включая их уникальный идентификатор в игре (InGameId),
	/// уникальный идентификатор в TeamSpeak (TeamSpeakId) и внутренний идентификатор (Id).
	/// Эта коллекция автоматически управляется Entity Framework Core и предоставляет возможности
	/// для выполнения операций CRUD (создание, чтение, обновление, удаление) в контексте базы данных.
	public DbSet<Gamer> Gamers => Set<Gamer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Gamer>(entity =>
        {
            entity.ToTable("Gamers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("Id");
            entity.Property(e => e.InGameId).HasColumnName("InGameId");
            entity.Property(e => e.TeamSpeakId).HasColumnName("TeamSpeakId").HasMaxLength(64);
        });
    }
}
