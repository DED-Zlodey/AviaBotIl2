using System;

namespace Registrator.DataModels;

public class Gamer
{
	/// <summary>
	/// Идентификатор записи
	/// </summary>
	public int Id { get; set; }

	/// <summary>
	/// Внутриигровой идентификатор игрока
	/// </summary>
	public Guid InGameId { get; set; }

	/// <summary>
	/// Идентификатор пользователя в системе TeamSpeak.
	/// </summary>
	public string? TeamSpeakId { get; set; }
}
