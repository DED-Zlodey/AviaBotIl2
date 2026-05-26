using AviaBot.Enums;
using System;

namespace AviaBot.Models;

/// <summary>
/// Класс, представляющий позицию игрока в игровом мире.
/// </summary>
public class PlayerPosition
{
	/// <summary>
	/// Уникальный идентификатор игрока в системе.
	/// Обеспечивает связь между данными игрока и его сессией.
	/// </summary>
	public string PlayerId { get; set; } = "";

	/// <summary>
	/// Имя игрока, используемое для идентификации в игровом процессе или коллективных операциях.
	/// </summary>
	public string GamerName { get; set; } = "";

	/// <summary>
	/// Уникальный идентификатор пользователя в TeamSpeak 3.
	/// Используется для идентификации игрока в системе и отправки сообщений через TeamSpeak 3.
	/// </summary>
	public string? Ts3Uid { get; set; }

	/// <summary>
	/// Координата X позиции объекта в игровом мире.
	/// </summary>
	public double X { get; set; }

	/// <summary>
	/// Координата Y позиции объекта в игровом мире.
	/// </summary>
	public double Y { get; set; }

	/// <summary>
	/// Координата Z позиции объекта в игровом мире.
	/// </summary>
	public double Z { get; set; }

	/// <summary>
	/// Коалиция, к которой принадлежит игрок.
	/// Используется для классификации игроков и фильтрации коммуникаций между ними.
	/// </summary>
	public int Coalition { get; set; }

	/// <summary>
	/// Время последнего обновления данных игрока.
	/// </summary>
	public DateTime LastUpdate { get; set; } = DateTime.UtcNow;

	/// <summary>
	/// Уникальный идентификатор процесса или объекта в игровом мире, используемый для сопоставления данных игрока.
	/// </summary>
	public int Pid { get; set; } = -1;

	/// <summary>
	/// Название объекта, с которым ассоциирован игрок.
	/// </summary>
	public string ObjectName { get; set; } = "";

	/// <summary>
	/// Тип объекта, связанный с позицией игрока в игровом мире.
	/// Определяет категорию или класс объекта, например, техника, постройка и т.д.
	/// </summary>
	public string TypeObject { get; set; } = "";

	/// <summary>
	/// true — игрок в лобби, false — в катке (активная игра).
	/// </summary>
	public bool IsInLobby { get; set; } = true;

	/// <summary>
	/// Категория объекта (aircraft, vehicle, Spectator и т.д.).
	/// </summary>
	public CategoryObject Category { get; set; } = CategoryObject.unknown;
}
