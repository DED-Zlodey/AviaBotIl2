using System;
using System.Text.Json.Serialization;

namespace AviaBot.Models;

/// <summary>
/// Модель позиции игрока, получаемая из RabbitMQ от Commander-Il2.
/// Соответствует формату JSON, который отправляет RabbitMqProducer.
/// </summary>
public class PlayerSession
{
	/// <summary>
	/// Событие (spawn, position, despawn и т.д.)
	/// </summary>
	[JsonPropertyName("event")]
	public string Event { get; set; } = string.Empty;

	/// <summary>
	/// Уникальный идентификатор игрока
	/// </summary>
	[JsonPropertyName("id")]
	public int Id { get; set; }

	/// <summary>
	/// ID родительского объекта (PID)
	/// </summary>
	[JsonPropertyName("pid")]
	public int Pid { get; set; } = -1;

	/// <summary>
	/// Код страны/коалиции
	/// </summary>
	[JsonPropertyName("country")]
	public int Country { get; set; }

	/// <summary>
	/// Имя игрока
	/// </summary>
	[JsonPropertyName("gamerName")]
	public string? GamerName { get; set; }

	/// <summary>
	/// Название объекта (самолёт, техника)
	/// </summary>
	[JsonPropertyName("name")]
	public string ObjectName { get; set; } = string.Empty;

	/// <summary>
	/// Тип объекта (aircraft, vehicle и т.д.)
	/// </summary>
	[JsonPropertyName("type")]
	public string TypeObject { get; set; } = string.Empty;

	/// <summary>
	/// Идентификатор TeamSpeak, связанный с игроком в текущей игровой сессии.
	/// Используется для синхронизации игрока с его учетной записью или действиями в коммуникационной системе TeamSpeak.
	/// </summary>
	public string? TeamSpeakId { get; set; }

	/// <summary>
	/// Координата X
	/// </summary>
	[JsonPropertyName("x")]
	public double X { get; set; }

	/// <summary>
	/// Координата Y
	/// </summary>
	[JsonPropertyName("y")]
	public double Y { get; set; }

	/// <summary>
	/// Координата Z (высота)
	/// </summary>
	[JsonPropertyName("z")]
	public double Z { get; set; }

	/// <summary>
	/// Время последнего обновления
	/// </summary>
	[JsonPropertyName("lastUpdate")]
	public DateTime LastUpdate { get; set; }

	/// <summary>
	/// Локализация/регион
	/// </summary>
	[JsonPropertyName("locale")]
	public string? Locale { get; set; }
}
