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
	private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<PlayerPositionService>();

	// --- Lobby (лобби) ---
	private readonly ConcurrentDictionary<string, PlayerPosition> _lobbyAllies = new();
	private readonly ConcurrentDictionary<string, PlayerPosition> _lobbyAxis = new();

	// --- Active (в катке) ---
	private readonly ConcurrentDictionary<string, PlayerPosition> _activeAllies = new();
	private readonly ConcurrentDictionary<string, PlayerPosition> _activeAxis = new();

	/// <summary>Обрабатывает событие join — добавляет игрока в лобби.</summary>
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

	/// <summary>Обрабатывает событие leave — полностью удаляет игрока.</summary>
	public void HandleLeave(PlayerSession session)
	{
		var name = NormalizeName(session.GamerName);
		if (RemoveFromAll(name))
			Log.Debug("HandleLeave: {Name} removed from all lists", session.GamerName);
	}

	/// <summary>Обрабатывает событие spawn — перемещает из лобби в катку.</summary>
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

	/// <summary>Обрабатывает событие despawn — перемещает из катки в лобби.</summary>
	public void HandleDespawn(PlayerSession session)
	{
		MoveToLobby(session);
	}

	/// <summary>Обрабатывает событие detach — перемещает из катки в лобби.</summary>
	public void HandleDetach(PlayerSession session)
	{
		MoveToLobby(session);
	}

	/// <summary>Обрабатывает событие position — обновляет координаты игрока в катке.</summary>
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

	/// <summary>Обрабатывает событие clear — очищает все списки.</summary>
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

	/// <summary>Добавляет или обновляет позицию в активном списке (для обратной совместимости).</summary>
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

	public bool TryRemove(string name)
	{
		return RemoveFromAll(NormalizeName(name));
	}

	public bool TryGetByName(string name, out PlayerPosition? session)
	{
		var key = NormalizeName(name);
		if (_lobbyAllies.TryGetValue(key, out session)) return true;
		if (_lobbyAxis.TryGetValue(key, out session)) return true;
		if (_activeAllies.TryGetValue(key, out session)) return true;
		return _activeAxis.TryGetValue(key, out session);
	}

	public IEnumerable<PlayerPosition> GetInSphere(int coalition, double centerX, double centerY, double centerZ, double radius)
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

	public IEnumerable<PlayerPosition> GetLobbyRecipients(int coalition)
	{
		var dict = GetLobbyDictionary(coalition);
		foreach (var session in dict.Values)
			yield return session;
	}

	public IEnumerable<PlayerPosition> GetAll()
	{
		foreach (var session in _lobbyAllies.Values) yield return session;
		foreach (var session in _lobbyAxis.Values) yield return session;
		foreach (var session in _activeAllies.Values) yield return session;
		foreach (var session in _activeAxis.Values) yield return session;
	}

	public void Clear()
	{
		HandleClear();
	}

	public bool IsInLobby(string name)
	{
		var key = NormalizeName(name);
		return _lobbyAllies.ContainsKey(key) || _lobbyAxis.ContainsKey(key);
	}

	public bool IsInGame(string name)
	{
		var key = NormalizeName(name);
		return _activeAllies.ContainsKey(key) || _activeAxis.ContainsKey(key);
	}

	// --- Private helpers ---

	private bool IsValidCoalition(int country)
	{
		return country == 101 || country == 201;
	}

	private bool RemoveFromAll(string key)
	{
		bool removed = false;
		removed |= _lobbyAllies.TryRemove(key, out _);
		removed |= _lobbyAxis.TryRemove(key, out _);
		removed |= _activeAllies.TryRemove(key, out _);
		removed |= _activeAxis.TryRemove(key, out _);
		return removed;
	}

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

	private ConcurrentDictionary<string, PlayerPosition> GetLobbyDictionary(int country)
	{
		return country == 101 ? _lobbyAllies : _lobbyAxis;
	}

	private ConcurrentDictionary<string, PlayerPosition> GetActiveDictionary(int country)
	{
		return country == 101 ? _activeAllies : _activeAxis;
	}

	private static string NormalizeName(string? name)
	{
		return (name ?? "").Trim().ToLowerInvariant();
	}
}
