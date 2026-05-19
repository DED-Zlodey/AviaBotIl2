using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using AviaBot.Enums;
using AviaBot.Models;

namespace AviaBot.Services;

/// <summary>
/// Высокопроизводительное потокобезопасное хранилище позиций игроков.
/// Поддерживает два состояния: Lobby (лобби) и Active (в катке).
/// Ключ — нормализованное имя игрока (GamerName).
/// </summary>
public class PlayerPositionService
{
	/// <summary>
	/// Логгер, используемый для записи потокобезопасных сообщений о состоянии и действиях сервиса управления позициями игроков.
	/// Хранит сообщения различного уровня, отладки, ошибок и информационных событий.
	/// Применяется для отслеживания процессов, включая вход игроков в лобби, выход,
	/// их респаун, перемещение и другие связанные события.
	/// </summary>
	private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<PlayerPositionService>();
	// --- Lobby (лобби) ---
	/// <summary>
	/// Потокобезопасное хранилище для отслеживания позиций союзных игроков, находящихся в лобби.
	/// Ключом является нормализованное имя игрока (GamerName), а значением — объект <see cref="PlayerPosition"/>,
	/// содержащий информацию о местоположении, статусе и других данных игрока.
	/// Используется для управления состоянием игроков до начала активной фазы игры.
	/// </summary>
	private readonly ConcurrentDictionary<string, PlayerPosition> _lobbyAllies = new();

	/// <summary>
	/// Потокобезопасное хранилище для отслеживания позиций игроков, принадлежащих коалиции Axis, находящихся в лобби.
	/// Ключом является нормализованное имя игрока (GamerName), а значением — объект <see cref="PlayerPosition"/>,
	/// содержащий данные о текущей позиции и состоянии игрока.
	/// Используется для управления состоянием игроков коалиции Axis до начала активной фазы игры.
	/// </summary>
	private readonly ConcurrentDictionary<string, PlayerPosition> _lobbyAxis = new();
	// --- Active (в катке) ---
	/// <summary>
	/// Потокобезопасное хранилище для отслеживания позиций союзных игроков, находящихся в активной фазе игры (в катке).
	/// Ключом является нормализованное имя игрока (GamerName), а значением — объект <see cref="PlayerPosition"/>,
	/// содержащий данные о позиции и состоянии игрока.
	/// Используется для управления состоянием игроков и быстрого доступа к их данным в рамках текущей катки.
	/// </summary>
	private readonly ConcurrentDictionary<string, PlayerPosition> _activeAllies = new();

	/// <summary>
	/// Хранилище позиций игроков команды Axis, находящихся в активной фазе (в катке).
	/// Потокобезопасный словарь, использующий нормализованные имена игроков в качестве ключей.
	/// </summary>
	private readonly ConcurrentDictionary<string, PlayerPosition> _activeAxis = new();

	/// <summary>
	/// Обрабатывает событие join — добавляет игрока в лобби.
	/// </summary>
	public void HandleJoin(PlayerSession session)
	{
		if (!IsValidCoalition(session.Country))
		{
			Log.Debug("HandleJoin: ignoring invalid coalition {Coalition} for {Name}", session.Country, session.GamerName);
			return;
		}

		var name = NormalizeName(session.GamerName);
		var lobbyDict = GetLobbyDictionary(session.Country);
		RemoveFromAll(name); // на всякий случай удаляем из всех списков

		var pos = CreatePosition(session, isInLobby: true);
		lobbyDict.TryAdd(name, pos);
		Log.Debug("HandleJoin: {Name} added to lobby (coalition={Coalition})", session.GamerName, session.Country);
	}

	/// <summary>
	/// Обрабатывает событие leave — полностью удаляет игрока.
	/// </summary>
	public void HandleLeave(PlayerSession session)
	{
		var name = NormalizeName(session.GamerName);
		if (RemoveFromAll(name))
			Log.Debug("HandleLeave: {Name} removed from all lists", session.GamerName);
	}

	/// <summary>
	/// Обрабатывает событие spawn — перемещает из лобби в катку.
	/// </summary>
	public void HandleSpawn(PlayerSession session)
	{
		if (!IsValidCoalition(session.Country))
			return;

		var name = NormalizeName(session.GamerName);
		var activeDict = GetActiveDictionary(session.Country);

		// Удаляем из лобби
		GetLobbyDictionary(session.Country).TryRemove(name, out _);

		var pos = CreatePosition(session, isInLobby: false);
		activeDict.TryAdd(name, pos);
		Log.Debug("HandleSpawn: {Name} moved to active (coalition={Coalition})", session.GamerName, session.Country);
	}

	/// <summary>
	/// Обрабатывает событие despawn — перемещает из катки в лобби.
	/// </summary>
	public void HandleDespawn(PlayerSession session)
	{
		MoveToLobby(session);
	}

	/// <summary>
	/// Обрабатывает событие detach — перемещает из катки в лобби.
	/// </summary>
	public void HandleDetach(PlayerSession session)
	{
		MoveToLobby(session);
	}

	/// <summary>
	/// Обрабатывает событие position — обновляет координаты игрока в катке.
	/// </summary>
	public void HandlePosition(PlayerSession session)
	{
		if (!IsValidCoalition(session.Country))
			return;

		var name = NormalizeName(session.GamerName);
		var activeDict = GetActiveDictionary(session.Country);

		if (activeDict.TryGetValue(name, out var existing))
		{
			existing.X = session.X;
			existing.Y = session.Y;
			existing.Z = session.Z;
			existing.LastUpdate = session.LastUpdate;
			existing.Pid = session.Pid;
			existing.ObjectName = session.ObjectName;
			existing.TypeObject = session.TypeObject;
			existing.Coalition = session.Country;
		}
		else
		{
			// Если не найден в active, возможно он в лобби — игнорируем позицию
			Log.Debug("HandlePosition: {Name} not found in active, ignoring position update", session.GamerName);
		}
	}

	/// <summary>
	/// Обрабатывает событие clear — очищает все списки.
	/// </summary>
	public void HandleClear()
	{
		_lobbyAllies.Clear();
		_lobbyAxis.Clear();
		_activeAllies.Clear();
		_activeAxis.Clear();
		Log.Information("HandleClear: all player lists cleared");
	}

	/// <summary>
	/// Универсальный метод обработки события. Определяет тип события и вызывает соответствующий обработчик.
	/// </summary>
	public void ProcessEvent(PlayerSession session)
	{
		switch (session.Event?.ToLowerInvariant())
		{
			case "join":
				HandleJoin(session);
				break;
			case "leave":
				HandleLeave(session);
				break;
			case "spawn":
				HandleSpawn(session);
				break;
			case "despawn":
				HandleDespawn(session);
				break;
			case "detach":
				HandleDetach(session);
				break;
			case "position":
				HandlePosition(session);
				break;
			case "clear":
				HandleClear();
				break;
			default:
				Log.Warning("ProcessEvent: unknown event '{Event}' for {Name}", session.Event, session.GamerName);
				break;
		}
	}

	// --- Legacy compatibility ---

	/// <summary>
	/// Добавляет или обновляет позицию в активном списке (для обратной совместимости).
	/// </summary>
	public void AddOrUpdate(PlayerSession session)
	{
		if (!IsValidCoalition(session.Country))
			return;

		var name = NormalizeName(session.GamerName);
		var dict = GetActiveDictionary(session.Country);

		if (dict.TryGetValue(name, out var existing))
		{
			existing.X = session.X;
			existing.Y = session.Y;
			existing.Z = session.Z;
			existing.LastUpdate = session.LastUpdate;
			existing.Coalition = session.Country;
			existing.Pid = session.Pid;
			existing.ObjectName = session.ObjectName;
			existing.TypeObject = session.TypeObject;
		}
		else
		{
			var pos = CreatePosition(session, isInLobby: false);
			dict.TryAdd(name, pos);
		}
	}

	/// <summary>
	/// Удаляет игрока из всех списков на основе его нормализованного имени.
	/// </summary>
	/// <param name="name">Имя игрока для удаления.</param>
	/// <returns>Возвращает true, если игрок был успешно удалён хотя бы из одного списка;
	/// иначе возвращает false.</returns>
	public bool TryRemove(string name)
	{
		return RemoveFromAll(NormalizeName(name));
	}

	/// <summary>
	/// Пытается получить позицию игрока по его имени.
	/// Выполняет поиск среди всех состояний: Lobby и Active.
	/// </summary>
	/// <param name="name">Имя игрока для нормализованного поиска.</param>
	/// <param name="session">Выходной параметр, содержащий найденную позицию игрока или null, если игрок не найден.</param>
	/// <returns>Возвращает true, если игрок найден, иначе false.</returns>
	public bool TryGetByName(string name, out PlayerPosition? session)
	{
		var key = NormalizeName(name);
		if (_lobbyAllies.TryGetValue(key, out session)) return true;
		if (_lobbyAxis.TryGetValue(key, out session)) return true;
		if (_activeAllies.TryGetValue(key, out session)) return true;
		return _activeAxis.TryGetValue(key, out session);
	}

	/// <summary>
	/// Возвращает список позиций игроков, находящихся внутри заданной сферы.
	/// </summary>
	/// <param name="coalition">Идентификатор коалиции, для которой производится поиск.</param>
	/// <param name="centerX">Координата центра сферы по оси X.</param>
	/// <param name="centerY">Координата центра сферы по оси Y.</param>
	/// <param name="centerZ">Координата центра сферы по оси Z.</param>
	/// <param name="radius">Радиус сферы поиска.</param>
	/// <returns>Перечисление позиций игроков, находящихся внутри сферы.</returns>
	public IEnumerable<PlayerPosition> GetInSphere(int coalition, double centerX, double centerY, double centerZ,
		double radius)
	{
		var dict = GetActiveDictionary(coalition);
		double radiusSquared = radius * radius;

		foreach (var session in dict.Values)
		{
			double dx = session.X - centerX;
			double dy = session.Y - centerY;
			double dz = session.Z - centerZ;

			if (dx * dx + dy * dy + dz * dz <= radiusSquared)
				yield return session;
		}
	}

	/// <summary>
	/// Возвращает всех игроков, находящихся в лобби, принадлежащих указанной коалиции.
	/// </summary>
	/// <param name="coalition">Код коалиции, по которому фильтруются игроки.</param>
	/// <returns>Перечисление объектов <see cref="PlayerPosition"/>, представляющих игроков в лобби.</returns>
	public IEnumerable<PlayerPosition> GetLobbyRecipients(int coalition)
	{
		var dict = GetLobbyDictionary(coalition);
		foreach (var session in dict.Values)
			yield return session;
	}

	/// <summary>
	/// Возвращает перечисление всех позиций игроков, находящихся в лобби и в активной игре.
	/// </summary>
	/// <returns>
	/// Перечисление объектов типа PlayerPosition, представляющих всех игроков из всех списков.
	/// </returns>
	public IEnumerable<PlayerPosition> GetAll()
	{
		foreach (var session in _lobbyAllies.Values) yield return session;
		foreach (var session in _lobbyAxis.Values) yield return session;
		foreach (var session in _activeAllies.Values) yield return session;
		foreach (var session in _activeAxis.Values) yield return session;
	}

	/// <summary>
	/// Очищает все списки игроков, включая списки лобби и активных игроков.
	/// </summary>
	public void Clear()
	{
		HandleClear();
	}

	/// <summary>
	/// Проверяет, находится ли игрок в лобби.
	/// </summary>
	/// <param name="name">Имя игрока, подлежащее проверке.</param>
	/// <returns>Значение true, если игрок находится в лобби, иначе false.</returns>
	public bool IsInLobby(string name)
	{
		var key = NormalizeName(name);
		return _lobbyAllies.ContainsKey(key) || _lobbyAxis.ContainsKey(key);
	}

	/// <summary>
	/// Проверяет, находится ли игрок в катке.
	/// </summary>
	/// <param name="name">Имя игрока.</param>
	/// <returns>Значение true, если игрок в катке; иначе false.</returns>
	public bool IsInGame(string name)
	{
		var key = NormalizeName(name);
		return _activeAllies.ContainsKey(key) || _activeAxis.ContainsKey(key);
	}

	// --- Private helpers ---

	/// <summary>
	/// Проверяет, является ли указанная коалиция допустимой.
	/// </summary>
	/// <param name="country">Идентификатор страны, представляющей коалицию.</param>
	/// <returns>Значение true, если коалиция допустима; в противном случае — false.</returns>
	private bool IsValidCoalition(int country)
	{
		return country == 101 || country == 201;
	}

	/// <summary>
	/// Удаляет игрока из всех списков.
	/// </summary>
	/// <param name="key">Нормализованное имя игрока.</param>
	/// <returns>Возвращает значение true, если игрок был удален из одного или нескольких списков; в противном случае false.</returns>
	private bool RemoveFromAll(string key)
	{
		bool removed = false;
		removed |= _lobbyAllies.TryRemove(key, out _);
		removed |= _lobbyAxis.TryRemove(key, out _);
		removed |= _activeAllies.TryRemove(key, out _);
		removed |= _activeAxis.TryRemove(key, out _);
		return removed;
	}

	/// <summary>
	/// Перемещает игрока из активного состояния в лобби при соблюдении допустимых условий.
	/// </summary>
	/// <param name="session">Сессия игрока, содержащая информацию о его текущем состоянии.</param>
	private void MoveToLobby(PlayerSession session)
	{
		if (!IsValidCoalition(session.Country))
			return;

		var name = NormalizeName(session.GamerName);
		var activeDict = GetActiveDictionary(session.Country);
		var lobbyDict = GetLobbyDictionary(session.Country);

		if (activeDict.TryRemove(name, out var pos))
		{
			pos.IsInLobby = true;
			pos.X = 0;
			pos.Y = 0;
			pos.Z = 0;
			pos.LastUpdate = DateTime.UtcNow;
			lobbyDict.TryAdd(name, pos);
			Log.Debug("MoveToLobby: {Name} moved from active to lobby", session.GamerName);
		}
	}

	/// <summary>
	/// Создает объект позиции игрока на основе данных из сессии.
	/// </summary>
	/// <param name="session">Сессия игрока, содержащая данные о его состоянии и расположении.</param>
	/// <param name="isInLobby">Флаг, указывающий, находится ли игрок в лобби.</param>
	/// <returns>Возвращает объект PlayerPosition, описывающий позицию игрока.</returns>
	private static PlayerPosition CreatePosition(PlayerSession session, bool isInLobby)
	{
		return new PlayerPosition
		{
			PlayerId = session.Id.ToString(),
			GamerName = session.GamerName ?? "",
			X = session.X,
			Y = session.Y,
			Z = session.Z,
			Coalition = session.Country,
			LastUpdate = session.LastUpdate,
			Pid = session.Pid,
			ObjectName = session.ObjectName,
			TypeObject = session.TypeObject,
			IsInLobby = isInLobby,
			Category = Enum.TryParse<CategoryObject>(session.TypeObject, true, out var cat) ? cat : CategoryObject.unknown
		};
	}

	/// <summary>
	/// Возвращает словарь позиций игроков, находящихся в лобби, для указанной коалиции.
	/// </summary>
	/// <param name="country">Идентификатор коалиции. Используется для выбора словаря (например, союзники или ось).</param>
	/// <return>Потокобезопасный словарь позиций игроков для указанной коалиции.</return>
	private ConcurrentDictionary<string, PlayerPosition> GetLobbyDictionary(int country)
	{
		return country == 101 ? _lobbyAllies : _lobbyAxis;
	}

	/// <summary>
	/// Возвращает активный словарь игроков в зависимости от коалиции.
	/// </summary>
	/// <param name="country">Код коалиции (например, 101 для союзников или другая валидная коалиция).</param>
	/// <returns>Потокобезопасный словарь активных игроков для указанной коалиции.</returns>
	private ConcurrentDictionary<string, PlayerPosition> GetActiveDictionary(int country)
	{
		return country == 101 ? _activeAllies : _activeAxis;
	}

	/// <summary>
	/// Нормализует имя игрока, выполняя обрезку пробелов и преобразование в нижний регистр.
	/// </summary>
	/// <param name="name">Имя игрока, которое требуется нормализовать.</param>
	/// <returns>Нормализованное имя игрока.</returns>
	private static string NormalizeName(string? name)
	{
		return (name ?? "").Trim().ToLowerInvariant();
	}
}
