using System;

namespace AviaBot.Models;

/// <summary>
/// Класс представляет сообщение, которое используется для передачи данных между пользователями.
/// </summary>
public class RelayMessage
{
	/// <summary>
	/// Уникальный ID отправителя (игровой)
	/// </summary>
	public string SenderId { get; set; } = "";

	/// <summary>
	/// TS3 UID отправителя (если известен)
	/// </summary>
	public string? SenderTs3Uid { get; set; }

	/// <summary>
	/// Текст сообщения или ссылка на аудио
	/// </summary>
	public string Text { get; set; } = "";

	/// <summary>
	/// Позиция отправителя
	/// </summary>
	public Position Position { get; set; } = new();

	/// <summary>
	/// Коалиция/фракция отправителя
	/// </summary>
	public int Coalition { get; set; }

	/// <summary>
	/// Время отправки (UTC)
	/// </summary>
	public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
