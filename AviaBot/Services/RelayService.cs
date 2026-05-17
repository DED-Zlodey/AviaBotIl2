using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AviaBot.Models;

namespace AviaBot.Services;

public class RelayService
{
	/// <summary>
	/// Логгер для записи информации о работе <see cref="RelayService"/>.
	/// Используется для отслеживания ключевых событий и отладки.
	/// </summary>
	private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<RelayService>();

	/// <summary>
	/// Сервис TeamSpeak 3, используемый для отправки личных сообщений игрокам через их уникальный идентификатор TS3.
	/// </summary>
	private readonly Ts3BotService _ts3Bot;

	/// <summary>
	/// Сервис для управления и обновления позиций игроков.
	/// Используется для поиска и обработки информации о местоположении игроков в игровом пространстве.
	/// </summary>
	private readonly PlayerPositionService _positionService;

	/// <summary>
	/// Настройки ретрансляции, содержащие параметры для работы сервиса,
	/// такие как максимальная дистанция взаимодействия и необходимость проверки коалиции.
	/// </summary>
	private readonly RelaySettings _settings;

	/// <summary>
	/// Квадрат максимального расстояния для быстрого отсечения без <see cref="Math.Sqrt"/>.
	/// </summary>
	private readonly double _maxDistanceSquared;

	/// <summary>
	/// Сервис ретрансляции сообщений между различными компонентами системы.
	/// Обеспечивает обработку входящих сообщений и управление позициями игроков.
	/// </summary>
	public RelayService(Ts3BotService ts3Bot, PlayerPositionService positionService, RelaySettings settings)
	{
		_ts3Bot = ts3Bot;
		_positionService = positionService;
		_settings = settings;
		_maxDistanceSquared = settings.MaxDistance * settings.MaxDistance;
	}

	/// <summary>
	/// Обрабатывает сеанс игрока, полученный из RabbitMQ.
	/// Выполняет обновление или добавление позиции игрока в сервисе управления позициями.
	/// </summary>
	/// <param name="session">Сеанс игрока, содержащий информацию о позиции, имени и других параметрах.</param>
	public void ProcessPlayerSession(PlayerSession session)
	{
		if (session == null)
		{
			Log.Warning("ProcessPlayerSession: session is null");
			return;
		}

		_positionService.AddOrUpdate(session);
		Log.Information("Position update from {0} ({1}): ({2:F1}, {3:F1}, {4:F1}) coalition={5}",
			session.Id, session.GamerName, session.X, session.Y, session.Z, session.Country);
	}

	/// <summary>
	/// Обработать входящее сообщение и разослать получателям
	/// </summary>
	public async Task ProcessMessageAsync(RelayMessage message)
	{
		// Обновляем позицию отправителя
		var senderPos = new PlayerPosition
		{
			PlayerId = message.SenderId,
			Ts3Uid = message.SenderTs3Uid,
			X = message.Position.X,
			Y = message.Position.Y,
			Z = message.Position.Z,
			Coalition = message.Coalition
		};
		_positionService.AddOrUpdate(new PlayerSession
		{
			Id = int.TryParse(message.SenderId, out var id) ? id : 0,
			GamerName = message.SenderId,
			X = message.Position.X,
			Y = message.Position.Y,
			Z = message.Position.Z,
			Country = message.Coalition,
			LastUpdate = DateTime.UtcNow
		});

		// Находим получателей
		var recipients = FindRecipients(senderPos);
		if (recipients.Count == 0)
		{
			Log.Debug("No recipients found for message from {0}", message.SenderId);
			return;
		}

		// Формируем текст с учётом расстояния (шум/искажение)
		foreach (var recipient in recipients)
		{
			var distSq = CalculateDistanceSquared(senderPos, recipient);
			string degradedText;
			double? distance = null;

			// Быстрый путь: расстояние ≤ 30 % от максимума — чистый сигнал без Math.Sqrt
			if (distSq <= 0.09 * _maxDistanceSquared)
			{
				degradedText = message.Text;
			}
			else
			{
				distance = Math.Sqrt(distSq);
				degradedText = DegradeText(message.Text, distance.Value);

				if (string.IsNullOrEmpty(degradedText))
				{
					Log.Debug("Message completely degraded for {0} at distance {1:F1}", recipient.PlayerId, distance.Value);
					continue;
				}
			}

			// Пытаемся отправить через TS3
			if (!string.IsNullOrEmpty(recipient.Ts3Uid))
			{
				var result = await _ts3Bot.SendPrivateMessageByUidAsync(recipient.Ts3Uid, degradedText);
				if (result)
				{
					if (Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
					{
						var d = distance ?? Math.Sqrt(distSq);
						Log.Debug("Sent to {0} (distance={1:F1}): {2}", recipient.PlayerId, d, degradedText);
					}
				}
				else
				{
					Log.Warning("Failed to send to {0}", recipient.PlayerId);
				}
			}
			else
			{
				Log.Warning("No TS3 UID for recipient {0}, cannot relay", recipient.PlayerId);
			}
		}
	}

	/// <summary>
	/// Найти получателей в радиусе от отправителя по имени
	/// </summary>
	public List<PlayerPosition> FindRecipientsByName(string senderName)
	{
		if (!_positionService.TryGetByName(senderName, out var sender) || sender == null)
			return new List<PlayerPosition>();

		return FindRecipients(sender);
	}

	/// <summary>
	/// Находит список игроков, которые могут быть получателями сообщения исходя из позиции отправителя.
	/// </summary>
	/// <param name="sender">Позиция игрока, отправляющего сообщение.</param>
	/// <returns>Список игроков, которые находятся в зоне действия отправителя и соответствуют заданным критериям.</returns>
	private List<PlayerPosition> FindRecipients(PlayerPosition sender)
	{
		var result = new List<PlayerPosition>();

		var candidates = _positionService.GetInSphere(
			sender.Coalition, sender.X, sender.Y, sender.Z, _settings.MaxDistance);

		foreach (var player in candidates)
		{
			// Пропускаем отправителя
			if (player.GamerName == sender.GamerName)
				continue;

			// Фильтр по коалиции
			if (_settings.CoalitionCheck && player.Coalition != sender.Coalition)
				continue;

			result.Add(player);
		}

		return result;
	}


	/// <summary>
	/// Вычисляет расстояние между двумя позициями игроков на основе их координат в 3D пространстве.
	/// </summary>
	/// <param name="a">Первая позиция игрока, содержащая координаты X, Y, Z.</param>
	/// <param name="b">Вторая позиция игрока, содержащая координаты X, Y, Z.</param>
	/// <returns>Расстояние между двумя позициями в виде числа с плавающей запятой.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static double CalculateDistance(PlayerPosition a, PlayerPosition b)
	{
		double dx = a.X - b.X;
		double dy = a.Y - b.Y;
		double dz = a.Z - b.Z;
		return Math.Sqrt(dx * dx + dy * dy + dz * dz);
	}


	/// <summary>
	/// Вычисляет квадрат расстояния между двумя точками в трёхмерном пространстве.
	/// Используется для оптимизации вычислений, исключая необходимость извлечения квадратного корня.
	/// </summary>
	/// <param name="a">Первая точка, заданная объектом PlayerPosition.</param>
	/// <param name="b">Вторая точка, заданная объектом PlayerPosition.</param>
	/// <returns>Квадрат расстояния между двумя точками.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static double CalculateDistanceSquared(PlayerPosition a, PlayerPosition b)
	{
		double dx = a.X - b.X;
		double dy = a.Y - b.Y;
		double dz = a.Z - b.Z;
		return dx * dx + dy * dy + dz * dz;
	}

	/// <summary>
	/// Деградация текста в зависимости от расстояния.
	/// На расстоянии 0% — чистый текст.
	/// На расстоянии 100% MaxDistance — полная потеря (пустая строка).
	/// Промежуточные — случайный шум в виде замены символов на '?'.
	/// </summary>
	private string DegradeText(string text, double distance)
	{
		if (string.IsNullOrEmpty(text))
			return text;

		var ratio = distance / _settings.MaxDistance;
		if (ratio <= 0.3)
			return text; // Чистый сигнал

		if (ratio >= 1.0)
			return string.Empty; // Полная потеря

		// Шум: чем дальше, тем больше символов заменяется
		var noiseLevel = (ratio - 0.3) / 0.7; // 0..1
		var random = new Random(text.GetHashCode() + (int)(distance * 100)); // Детерминированный шум
		var chars = text.ToCharArray();

		for (int i = 0; i < chars.Length; i++)
		{
			if (char.IsWhiteSpace(chars[i]))
				continue;

			if (random.NextDouble() < noiseLevel * 0.6)
			{
				chars[i] = '?';
			}
			else if (random.NextDouble() < noiseLevel * 0.3)
			{
				chars[i] = (char)random.Next('a', 'z' + 1);
			}
		}

		return new string(chars);
	}
}

public class RelaySettings
{
	public double MaxDistance { get; set; } = 1000.0;
	public bool CoalitionCheck { get; set; } = true;

	/// <summary>Таймаут определения конца речи по отсутствию пакетов (мс).</summary>
	public int SpeechTimeoutMs { get; set; } = 350;

	/// <summary>Включить ретрансляцию в лобби (без ограничения дистанции).</summary>
	public bool LobbyEnabled { get; set; } = true;

	/// <summary>Включить радио-эффекты (шум, затухание, dropout).</summary>
	public bool RadioEffectsEnabled { get; set; } = false;

	/// <summary>Дискретные уровни качества радиосвязи по дальности.</summary>
	public List<RadioQualityLevel> RadioQualityLevels { get; set; } = new()
	{
		new() { MaxFactor = 0.15, Attenuation = 0.00, Noise = 0.02, CrushBits = 0, Dropout = 0.00 },
		new() { MaxFactor = 0.30, Attenuation = 0.10, Noise = 0.05, CrushBits = 1, Dropout = 0.00 },
		new() { MaxFactor = 0.50, Attenuation = 0.25, Noise = 0.10, CrushBits = 2, Dropout = 0.05 },
		new() { MaxFactor = 0.70, Attenuation = 0.45, Noise = 0.20, CrushBits = 3, Dropout = 0.15 },
		new() { MaxFactor = 0.85, Attenuation = 0.65, Noise = 0.35, CrushBits = 4, Dropout = 0.35 },
		new() { MaxFactor = 0.95, Attenuation = 0.80, Noise = 0.50, CrushBits = 5, Dropout = 0.60 },
		new() { MaxFactor = 1.00, Attenuation = 1.00, Noise = 0.70, CrushBits = 6, Dropout = 0.90 },
	};
}
