using AviaBot.Services;
using System.Collections.Generic;

namespace AviaBot.Models;

public class RelaySettings
{
	/// <summary>
	/// Максимальная дистанция в метрах, на которой передача сообщений возможна
	/// в рамках работы <see cref="RelayService"/>.
	/// Используется для расчёта доступных получателей и
	/// снижения качества передачи сигнала при увеличении расстояния.
	/// </summary>
	public double MaxDistance { get; set; } = 1000.0;

	/// <summary>
	/// Указывает, требуется ли учитывать коалицию игроков при выборе получателей сообщений.
	/// Если значение установлено в <c>true</c>, то сообщения будут передаваться только игрокам из той же коалиции,
	/// что и отправитель.
	/// </summary>
	public bool CoalitionCheck { get; set; } = true;
}
