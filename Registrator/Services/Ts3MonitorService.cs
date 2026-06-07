using Registrator.Data;
using Registrator.DataModels;
using Registrator.RabbitMq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Registrator.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TSLib.Full;
using TSLib.Messages;

namespace Registrator.Services;

/// <summary>
/// Служба мониторинга событий клиента TeamSpeak.
/// </summary>
/// <remarks>
/// Реализует интерфейс <see cref="IHostedService"/> для управления жизненным циклом сервиса.
/// Слушает события <see cref="ITs3ClientAccessor.ClientReady"/> и <see cref="ITs3ClientAccessor.ClientLost"/>
/// для отслеживания состояния клиента TeamSpeak, а также выполняет соответствующие действия при подключении
/// или отключении клиента.
/// </remarks>
public class Ts3MonitorService : IHostedService
{
	/// <summary>
	/// Логгер для записи сообщений о работе службы.
	/// </summary>
	/// <remarks>
	/// Используется для регистрации информации, предупреждений, ошибок и других событий в процессе выполнения службы.
	/// Обеспечивает информационное сопровождение обработки событий, таких как создание, изменение, удаление или получение списка каналов.
	/// </remarks>
	private static readonly ILogger Log = Serilog.Log.ForContext<Ts3MonitorService>();

	/// <summary>
	/// Объект для управления доступом к клиенту TeamSpeak.
	/// </summary>
	/// <remarks>
	/// Реализует интерфейс <see cref="ITs3ClientAccessor"/> для предоставления информации о текущем состоянии клиента TeamSpeak,
	/// а также для управления подключением и отключением клиента посредством событий <see cref="ITs3ClientAccessor.ClientReady"/>
	/// и <see cref="ITs3ClientAccessor.ClientLost"/>. Используется для интеграции клиентских событий
	/// в службу мониторинга <see cref="Ts3MonitorService"/>.
	/// </remarks>
	private readonly ITs3ClientAccessor _clientAccessor;

	/// <summary>
	/// Фабрика для создания контекстов базы данных.
	/// </summary>
	/// <remarks>
	/// Предоставляет экземпляры контекста базы данных <c>ApplicationDbContext</c>, которые используются для работы с таблицами базы данных, связанными с данными TeamSpeak.
	/// Фабрика обеспечивает изоляцию контекстов базы данных для обработки запросов, таких как сброс состояния пользователей онлайн, управление каналами и сессиями TeamSpeak.
	/// Упрощает управление временем жизни контекста и облегчает работу с асинхронными операциями.
	/// </remarks>
	private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

	private readonly ITs3EventPublisher _publisher;

	/// <summary>
	/// Служба мониторинга событий клиента TeamSpeak, реализующая интерфейс IHostedService.
	/// </summary>
	/// <remarks>
	/// Отслеживает подключение и отключение клиента TeamSpeak через события <see cref="ITs3ClientAccessor.ClientReady"/>
	/// и <see cref="ITs3ClientAccessor.ClientLost"/>. При готовности клиента выполняет дополнительные операции
	/// через методы <see cref="OnClientReady"/> и <see cref="OnClientLost"/>.
	/// </remarks>
	public Ts3MonitorService(ITs3ClientAccessor clientAccessor, IDbContextFactory<AppDbContext> dbContextFactory,
		ITs3EventPublisher publisher)
	{
		_clientAccessor = clientAccessor;
		_dbContextFactory = dbContextFactory;
		_publisher = publisher;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_clientAccessor.ClientReady += OnClientReady;
		_clientAccessor.ClientLost += OnClientLost;

		if (_clientAccessor.Client != null)
			OnClientReady(this, _clientAccessor.Client);

		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_clientAccessor.ClientReady -= OnClientReady;
		_clientAccessor.ClientLost -= OnClientLost;
		return Task.CompletedTask;
	}

	/// <summary>
	/// Обрабатывает событие готовности клиента TeamSpeak.
	/// </summary>
	/// <param name="sender">Источник события, обычно равен null.</param>
	/// <param name="client">Экземпляр клиента TeamSpeak, который стал готов к работе.</param>
	/// <remarks>
	/// Метод выполняет начальную настройку клиента, такую как сброс состояния подключения и
	/// подписка на события изменения каналов и событий клиентов:
	/// <see cref="TsFullClient.OnEachChannelList"/>, <see cref="TsFullClient.OnEachChannelCreated"/>,
	/// <see cref="TsFullClient.OnEachChannelEdited"/>, <see cref="TsFullClient.OnEachChannelDeleted"/>,
	/// <see cref="TsFullClient.OnEachChannelMoved"/>, <see cref="TsFullClient.OnEachClientEnterView"/>,
	/// <see cref="TsFullClient.OnEachClientLeftView"/> и <see cref="TsFullClient.OnEachClientMoved"/>.
	/// </remarks>
	private async void OnClientReady(object? sender, TsFullClient client)
	{
		try
		{
			await ResetOnlineStateAsync();

			client.OnEachChannelList += OnChannelList;
			client.OnEachChannelCreated += OnChannelCreated;
			client.OnEachChannelEdited += OnChannelEdited;
			client.OnEachChannelDeleted += OnChannelDeleted;
			client.OnEachChannelMoved += OnChannelMoved;

			client.OnEachClientEnterView += OnClientEnterView;
			client.OnEachClientLeftView += OnClientLeftView;
			client.OnEachClientMoved += OnClientMoved;
			client.OnEachClientUpdated += OnClientUpdated;

			await SyncInitialStateAsync(client);
		}
		catch (Exception ex)
		{
			Log.Error(ex, "{method} Error setting up client", nameof(OnClientReady));
		}
	}

	/// <summary>
	/// Обрабатывает отключение клиента TeamSpeak и выполняет очистку событий клиента.
	/// </summary>
	/// <param name="sender">Источник события, инициировавший вызов метода.</param>
	/// <param name="e">Аргументы события не используются в данном методе.</param>
	/// <remarks>
	/// Метод вызывается при срабатывании события <see cref="ITs3ClientAccessor.ClientLost"/>.
	/// Выполняется отписка от всех событий клиента, чтобы предотвратить некорректное поведение
	/// после отключения. После этого вызывается метод <see cref="ResetOnlineStateAsync"/> для сброса состояния.
	/// </remarks>
	private void OnClientLost(object? sender, EventArgs e)
	{
		var client = _clientAccessor.Client;
		if (client == null) return;

		client.OnEachChannelList -= OnChannelList;
		client.OnEachChannelCreated -= OnChannelCreated;
		client.OnEachChannelEdited -= OnChannelEdited;
		client.OnEachChannelDeleted -= OnChannelDeleted;
		client.OnEachChannelMoved -= OnChannelMoved;

		client.OnEachClientEnterView -= OnClientEnterView;
		client.OnEachClientLeftView -= OnClientLeftView;
		client.OnEachClientMoved -= OnClientMoved;
		client.OnEachClientUpdated -= OnClientUpdated;

		_ = ResetOnlineStateAsync("Bot disconnected");
	}

	/// <summary>
	/// Выполняет сброс онлайнового состояния пользователей, сессий и каналов TeamSpeak.
	/// </summary>
	/// <param name="reason">Причина сброса состояния. Если не указана, используется значение по умолчанию "Bot reconnected".</param>
	/// <returns>
	/// Асинхронная задача, представляющая процесс сброса состояния.
	/// </returns>
	/// <remarks>
	/// Метод производит обновление данных в базе данных, устанавливая состояния «не в сети» для пользователей, завершая зависшие сессии
	/// и сбрасывая количество активных клиентов в каналах. Вызывается, как правило, при отключении или перезапуске клиента TeamSpeak.
	/// В случае возникновения исключения ошибка логируется.
	/// </remarks>
	private async Task ResetOnlineStateAsync(string? reason = null)
	{
		try
		{
			await using var db = await _dbContextFactory.CreateDbContextAsync();

			var hangingSessions = await db.TeamSpeakSessions
				.Where(s => s.DisconnectedAt == null)
				.ToListAsync();

			foreach (var session in hangingSessions)
			{
				session.DisconnectedAt = DateTimeOffset.UtcNow;
				session.DisconnectReason = reason ?? "Bot reconnected";
			}

			var onlineUsers = await db.TeamSpeakUsers
				.Where(u => u.IsOnline)
				.ToListAsync();

			foreach (var user in onlineUsers)
			{
				user.IsOnline = false;
				user.CurrentClientId = null;
				user.CurrentTsChannelId = null;
				user.LastDisconnectedAt = DateTimeOffset.UtcNow;
			}

			var channels = await db.TeamSpeakChannels.ToListAsync();
			foreach (var channel in channels)
				channel.CurrentClientCount = 0;

			await db.SaveChangesAsync();
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error resetting online state");
		}
	}

	/// <summary>
	/// Синхронизирует текущее состояние сервера из Book с базой данных после подключения.
	/// </summary>
	/// <param name="client">Клиент TeamSpeak.</param>
	private async Task SyncInitialStateAsync(TsFullClient client)
	{
		try
		{
			await using var db = await _dbContextFactory.CreateDbContextAsync();

			// Sync channels from Book
			foreach (var kvp in client.Book.Channels)
			{
				var bookCh = kvp.Value;
				var tsId = (int)bookCh.Id.Value;
				var channel = await db.TeamSpeakChannels.FirstOrDefaultAsync(c => c.TsChannelId == tsId);

				int? maxClients = null;
				if (bookCh.MaxClients.HasValue)
				{
					maxClients = bookCh.MaxClients.Value.LimitKind == TSLib.Full.Book.MaxClientsKind.Unlimited
						? null
						: bookCh.MaxClients.Value.Count;
				}

				if (channel == null)
				{
					db.TeamSpeakChannels.Add(new TeamSpeakChannel
					{
						TsChannelId = tsId,
						Name = bookCh.Name,
						ParentTsChannelId = bookCh.Parent.Value == 0 ? null : (int)bookCh.Parent.Value,
						Order = (int)bookCh.Order.Value,
						Topic = bookCh.Topic,
						IsPermanent = false,
						IsSemiPermanent = false,
						IsPasswordProtected = bookCh.HasPassword ?? false,
						MaxClients = maxClients,
						CurrentClientCount = 0,
						IsDeleted = false,
						CreatedAt = DateTimeOffset.UtcNow,
						UpdatedAt = DateTimeOffset.UtcNow
					});
				}
				else
				{
					channel.Name = bookCh.Name;
					channel.ParentTsChannelId = bookCh.Parent.Value == 0 ? null : (int)bookCh.Parent.Value;
					channel.Order = (int)bookCh.Order.Value;
					channel.Topic = bookCh.Topic;
					channel.IsPasswordProtected = bookCh.HasPassword ?? false;
					channel.MaxClients = maxClients;
					channel.IsDeleted = false;
					channel.CurrentClientCount = 0;
					channel.UpdatedAt = DateTimeOffset.UtcNow;
				}
			}

			// Sync clients from Book and count clients per channel
			var clientCounts = new Dictionary<int, int>();
			foreach (var kvp in client.Book.Clients)
			{
				var bookCl = kvp.Value;
				if (bookCl.Id == client.Book.OwnClient) continue;
				var uid = bookCl.Uid?.Value;
				if (string.IsNullOrEmpty(uid)) continue;

				var clid = (int)bookCl.Id.Value;
				var chid = (int)bookCl.Channel.Value;
				clientCounts[chid] = clientCounts.GetValueOrDefault(chid) + 1;

				var user = await db.TeamSpeakUsers.FirstOrDefaultAsync(u => u.TsUniqueId == uid);
				if (user == null)
				{
					db.TeamSpeakUsers.Add(new TeamSpeakUser
					{
						TsUniqueId = uid,
						Nickname = bookCl.Name,
						CurrentClientId = clid,
						CurrentTsChannelId = chid,
						IsOnline = true,
						LastConnectedAt = DateTimeOffset.UtcNow,
						TotalConnections = 0,
						Country = bookCl.CountryCode,
						IsInputMuted = bookCl.InputMuted,
						IsOutputMuted = bookCl.OutputMuted,
						CreatedAt = DateTimeOffset.UtcNow,
						UpdatedAt = DateTimeOffset.UtcNow
					});
				}
				else
				{
					user.Nickname = bookCl.Name;
					user.CurrentClientId = clid;
					user.CurrentTsChannelId = chid;
					user.IsOnline = true;
					user.Country = bookCl.CountryCode;
					user.IsInputMuted = bookCl.InputMuted;
					user.IsOutputMuted = bookCl.OutputMuted;
					user.UpdatedAt = DateTimeOffset.UtcNow;
				}
			}

			await db.SaveChangesAsync();

			// Update channel client counts
			foreach (var kvp in clientCounts)
			{
				var channel = await db.TeamSpeakChannels.FirstOrDefaultAsync(c => c.TsChannelId == kvp.Key);
				if (channel != null)
					channel.CurrentClientCount = kvp.Value;
			}

			// Create sessions for online clients
			foreach (var kvp in client.Book.Clients)
			{
				var bookCl = kvp.Value;
				if (bookCl.Id == client.Book.OwnClient) continue;
				var uid = bookCl.Uid?.Value;
				if (string.IsNullOrEmpty(uid)) continue;

				var user = await db.TeamSpeakUsers.FirstOrDefaultAsync(u => u.TsUniqueId == uid);
				if (user == null) continue;

				var existingSession = await db.TeamSpeakSessions
					.FirstOrDefaultAsync(s => s.UserId == user.Id && s.DisconnectedAt == null);

				if (existingSession == null)
				{
					db.TeamSpeakSessions.Add(new TeamSpeakSession
					{
						UserId = user.Id,
						Nickname = bookCl.Name,
						InitialTsChannelId = (int)bookCl.Channel.Value,
						ConnectedAt = DateTimeOffset.UtcNow
					});
				}

				var existingLog = await db.UserAudioStateLogs
					.FirstOrDefaultAsync(l =>
						l.UserId == user.Id && l.ChangedAt > DateTimeOffset.UtcNow.AddMinutes(-1));

				if (existingLog == null)
				{
					db.UserAudioStateLogs.Add(new UserAudioStateLog
					{
						UserId = user.Id,
						IsInputMuted = bookCl.InputMuted,
						IsOutputMuted = bookCl.OutputMuted,
						ChangedAt = DateTimeOffset.UtcNow
					});
				}
			}

			await db.SaveChangesAsync();
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error syncing initial state from Book");
		}
	}

	/// <summary>
	/// Обрабатывает список каналов, предоставленный клиентом TeamSpeak, добавляет или обновляет информацию о каналах в базе данных.
	/// </summary>
	/// <param name="sender">Источник события, может быть null.</param>
	/// <param name="e">Объект ChannelList, содержащий информацию о каналах.</param>
	private async void OnChannelList(object? sender, ChannelList e)
	{
		try
		{
			await using var db = await _dbContextFactory.CreateDbContextAsync();
			var tsId = (int)e.ChannelId.Value;
			var channel = await db.TeamSpeakChannels.FirstOrDefaultAsync(c => c.TsChannelId == tsId);

			if (channel == null)
			{
				db.TeamSpeakChannels.Add(new TeamSpeakChannel
				{
					TsChannelId = tsId,
					Name = e.Name,
					ParentTsChannelId = e.ChannelId.Value == 0 ? null : (int)e.ParentId.Value,
					Order = (int)e.Order.Value,
					Topic = e.Topic,
					IsPermanent = e.IsPermanent,
					IsSemiPermanent = e.IsSemiPermanent,
					IsPasswordProtected = e.HasPassword,
					MaxClients = e.IsMaxClientsUnlimited ? null : e.MaxClients,
					CurrentClientCount = 0,
					IsDeleted = false,
					CreatedAt = DateTimeOffset.UtcNow,
					UpdatedAt = DateTimeOffset.UtcNow
				});
			}
			else
			{
				channel.Name = e.Name;
				channel.ParentTsChannelId = e.ChannelId.Value == 0 ? null : (int)e.ParentId.Value;
				channel.Order = (int)e.Order.Value;
				channel.Topic = e.Topic;
				channel.IsPermanent = e.IsPermanent;
				channel.IsSemiPermanent = e.IsSemiPermanent;
				channel.IsPasswordProtected = e.HasPassword;
				channel.MaxClients = e.IsMaxClientsUnlimited ? null : e.MaxClients;
				channel.IsDeleted = false;
				channel.UpdatedAt = DateTimeOffset.UtcNow;
			}

			await db.SaveChangesAsync();
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error handling ChannelList for channel {ChannelId}", e.ChannelId);
		}
	}

	/// <summary>
	/// Обрабатывает событие создания нового канала в TeamSpeak, добавляет информацию о новом канале в базу данных.
	/// </summary>
	/// <param name="sender">Источник события, может быть null.</param>
	/// <param name="e">Объект ChannelCreated, содержащий информацию о новом канале.</param>
	private async void OnChannelCreated(object? sender, ChannelCreated e)
	{
		try
		{
			await using var db = await _dbContextFactory.CreateDbContextAsync();
			var tsId = (int)e.ChannelId.Value;
			var channel = await db.TeamSpeakChannels.FirstOrDefaultAsync(c => c.TsChannelId == tsId);

			if (channel == null)
			{
				db.TeamSpeakChannels.Add(new TeamSpeakChannel
				{
					TsChannelId = tsId,
					Name = e.Name,
					ParentTsChannelId = e.ChannelId.Value == 0 ? null : (int)e.ParentId.Value,
					Order = (int)e.Order.Value,
					Topic = e.Topic,
					IsPermanent = e.IsPermanent ?? false,
					IsSemiPermanent = e.IsSemiPermanent ?? false,
					IsPasswordProtected = e.HasPassword ?? false,
					MaxClients = e.IsMaxClientsUnlimited == true ? null : e.MaxClients,
					CurrentClientCount = 0,
					IsDeleted = false,
					CreatedAt = DateTimeOffset.UtcNow,
					UpdatedAt = DateTimeOffset.UtcNow
				});
			}
			else
			{
				channel.Name = e.Name;
				channel.ParentTsChannelId = e.ChannelId.Value == 0 ? null : (int)e.ParentId.Value;
				channel.Order = (int)e.Order.Value;
				channel.Topic = e.Topic ?? channel.Topic;
				channel.IsPermanent = e.IsPermanent ?? channel.IsPermanent;
				channel.IsSemiPermanent = e.IsSemiPermanent ?? channel.IsSemiPermanent;
				channel.IsPasswordProtected = e.HasPassword ?? channel.IsPasswordProtected;
				if (e.IsMaxClientsUnlimited.HasValue || e.MaxClients.HasValue)
					channel.MaxClients = e.IsMaxClientsUnlimited == true ? null : e.MaxClients;
				channel.IsDeleted = false;
				channel.UpdatedAt = DateTimeOffset.UtcNow;
			}

			await db.SaveChangesAsync();
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error handling ChannelCreated for channel {ChannelId}", e.ChannelId);
		}
	}

	/// <summary>
	/// Метод для обработки события изменения канала в TeamSpeak.
	/// </summary>
	/// <param name="sender">Источник события, который вызвал обработчик.</param>
	/// <param name="e">Объект <see cref="ChannelEdited"/>, содержащий данные об изменении канала.</param>
	/// <remarks>
	/// Обновляет данные канала в базе данных, такие как название, родительский канал, порядок, тему,
	/// параметры постоянности, наличие пароля, максимальное количество клиентов и дату обновления.
	/// В случае ошибки регистрирует сообщение об ошибке в журнале.
	/// </remarks>
	private async void OnChannelEdited(object? sender, ChannelEdited e)
	{
		try
		{
			await using var db = await _dbContextFactory.CreateDbContextAsync();
			var tsId = (int)e.ChannelId.Value;
			var channel = await db.TeamSpeakChannels.FirstOrDefaultAsync(c => c.TsChannelId == tsId);

			if (channel == null) return;

			if (e.Name != null) channel.Name = e.Name;
			if (e.ParentId.HasValue)
				channel.ParentTsChannelId = e.ChannelId.Value == 0 ? null : (int)e.ParentId.Value.Value;
			if (e.Order.HasValue) channel.Order = (int)e.Order.Value.Value;
			if (e.Topic != null) channel.Topic = e.Topic;
			if (e.IsPermanent.HasValue) channel.IsPermanent = e.IsPermanent.Value;
			if (e.IsSemiPermanent.HasValue) channel.IsSemiPermanent = e.IsSemiPermanent.Value;
			if (e.HasPassword.HasValue) channel.IsPasswordProtected = e.HasPassword.Value;
			if (e.IsMaxClientsUnlimited.HasValue || e.MaxClients.HasValue)
				channel.MaxClients = e.IsMaxClientsUnlimited == true ? null : e.MaxClients;
			channel.UpdatedAt = DateTimeOffset.UtcNow;

			await db.SaveChangesAsync();
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error handling ChannelEdited for channel {ChannelId}", e.ChannelId);
		}
	}

	/// <summary>
	/// Обрабатывает событие удаления канала в клиенте TeamSpeak.
	/// </summary>
	/// <param name="sender">Источник события, может быть null.</param>
	/// <param name="e">Данные события, связанные с удаленным каналом.</param>
	/// <remarks>
	/// Метод обновляет запись удаленного канала в базе данных, устанавливая флаг удаления,
	/// сбрасывая текущее количество пользователей и обновляя временную метку последнего изменения.
	/// Если канал отсутствует в базе данных, изменения не применяются.
	/// В случае ошибки при обработке события записывает информацию об ошибке в журнал.
	/// </remarks>
	private async void OnChannelDeleted(object? sender, ChannelDeleted e)
	{
		try
		{
			await using var db = await _dbContextFactory.CreateDbContextAsync();
			var tsId = (int)e.ChannelId.Value;
			var channel = await db.TeamSpeakChannels.FirstOrDefaultAsync(c => c.TsChannelId == tsId);

			if (channel != null)
			{
				channel.IsDeleted = true;
				channel.CurrentClientCount = 0;
				channel.UpdatedAt = DateTimeOffset.UtcNow;
				await db.SaveChangesAsync();
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error handling ChannelDeleted for channel {ChannelId}", e.ChannelId);
		}
	}

	/// <summary>
	/// Обрабатывает событие перемещения канала в TeamSpeak.
	/// </summary>
	/// <param name="sender">Источник события, который обычно является клиентом TeamSpeak.</param>
	/// <param name="e">Данные события, содержащие информацию о перемещении канала.</param>
	/// <remarks>
	/// Указывает родительский канал, порядок сортировки и обновляет время последнего изменения
	/// для соответствующего канала в базе данных. В случае возникновения ошибки логируется
	/// сообщение об ошибке с подробной информацией.
	/// </remarks>
	private async void OnChannelMoved(object? sender, ChannelMoved e)
	{
		try
		{
			await using var db = await _dbContextFactory.CreateDbContextAsync();
			var tsId = (int)e.ChannelId.Value;
			var channel = await db.TeamSpeakChannels.FirstOrDefaultAsync(c => c.TsChannelId == tsId);

			if (channel != null)
			{
				channel.ParentTsChannelId = e.ChannelId.Value == 0 ? null : (int)e.ParentId.Value;
				channel.Order = (int)e.Order.Value;
				channel.UpdatedAt = DateTimeOffset.UtcNow;
				await db.SaveChangesAsync();
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error handling ChannelMoved for channel {ChannelId}", e.ChannelId);
		}
	}

	/// <summary>
	/// Обработчик события входа клиента в зону видимости TeamSpeak.
	/// </summary>
	/// <param name="sender">Источник события (обычно объект клиента TeamSpeak).</param>
	/// <param name="e">Данные события <see cref="ClientEnterView"/>, содержащие информацию о клиенте, его идентификаторе и целевом канале.</param>
	/// <remarks>
	/// Выполняет синхронизацию данных клиента с базой данных, включая поиск или создание записи пользователя, обновление активных сессий
	/// и проверку целевого канала. В случае ошибки логирует её с помощью системы логирования.
	/// </remarks>
	private async void OnClientEnterView(object? sender, ClientEnterView e)
	{
		try
		{
			await using var db = await _dbContextFactory.CreateDbContextAsync();
			var uid = e.Uid.Value;
			var clid = (int)e.ClientId.Value;
			var chid = (int)e.TargetChannelId.Value;

			var user = await db.TeamSpeakUsers.FirstOrDefaultAsync(u => u.TsUniqueId == uid);

			if (user == null)
			{
				user = new TeamSpeakUser
				{
					TsUniqueId = uid,
					Nickname = e.Name,
					CurrentClientId = clid,
					CurrentTsChannelId = chid,
					IsOnline = true,
					LastConnectedAt = DateTimeOffset.UtcNow,
					TotalConnections = 1,
					Country = e.CountryCode,
					IsInputMuted = e.InputMuted,
					IsOutputMuted = e.OutputMuted,
					CreatedAt = DateTimeOffset.UtcNow,
					UpdatedAt = DateTimeOffset.UtcNow
				};
				db.TeamSpeakUsers.Add(user);
			}
			else
			{
				user.Nickname = e.Name;
				user.CurrentClientId = clid;
				user.CurrentTsChannelId = chid;
				user.IsOnline = true;
				user.LastConnectedAt = DateTimeOffset.UtcNow;
				if (!user.IsOnline) user.TotalConnections++;
				user.Country = e.CountryCode;
				user.IsInputMuted = e.InputMuted;
				user.IsOutputMuted = e.OutputMuted;
				user.UpdatedAt = DateTimeOffset.UtcNow;
			}

			await db.SaveChangesAsync();

			var connectedPayload = new TsUserConnectedEvent
			{
				ClientId = clid,
				UniqueId = uid,
				Nickname = e.Name,
				ChannelId = chid,
				CountryCode = e.CountryCode,
				IsInputMuted = e.InputMuted,
				IsOutputMuted = e.OutputMuted
			};
			Log.Information("Publishing userConnected: {@Payload}", connectedPayload);
			_ = _publisher.PublishAsync(new Ts3EventEnvelope
			{
				EventType = "userConnected",
				Payload = connectedPayload
			});

			var existingSession = await db.TeamSpeakSessions
				.FirstOrDefaultAsync(s => s.UserId == user.Id && s.DisconnectedAt == null);

			if (existingSession == null)
			{
				db.TeamSpeakSessions.Add(new TeamSpeakSession
				{
					UserId = user.Id,
					Nickname = e.Name,
					InitialTsChannelId = chid,
					ConnectedAt = DateTimeOffset.UtcNow
				});
				await db.SaveChangesAsync();
			}

			db.UserAudioStateLogs.Add(new UserAudioStateLog
			{
				UserId = user.Id,
				SessionId = existingSession?.Id,
				IsInputMuted = e.InputMuted,
				IsOutputMuted = e.OutputMuted,
				ChangedAt = DateTimeOffset.UtcNow
			});
			await db.SaveChangesAsync();

			var channel = await db.TeamSpeakChannels.FirstOrDefaultAsync(c => c.TsChannelId == chid);
			if (channel != null)
			{
				channel.CurrentClientCount++;
				await db.SaveChangesAsync();
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error handling ClientEnterView for client {ClientId}", e.ClientId);
		}
	}

	/// <summary>
	/// Обрабатывает событие выхода клиента из видимости на сервере TeamSpeak.
	/// </summary>
	/// <param name="sender">Источник события, инициировавший вызов обработчика.</param>
	/// <param name="e">Данные события, связанные с покинувшим клиентом, инкапсулированные в объекте <see cref="ClientLeftView"/>.</param>
	private async void OnClientLeftView(object? sender, ClientLeftView e)
	{
		try
		{
			await using var db = await _dbContextFactory.CreateDbContextAsync();
			var clid = (int)e.ClientId.Value;
			var chid = (int)e.SourceChannelId.Value;

			var user = await db.TeamSpeakUsers.FirstOrDefaultAsync(u => u.CurrentClientId == clid);

			if (user != null)
			{
				user.IsOnline = false;
				user.CurrentClientId = null;
				user.CurrentTsChannelId = null;
				user.LastDisconnectedAt = DateTimeOffset.UtcNow;
				user.UpdatedAt = DateTimeOffset.UtcNow;

				var session = await db.TeamSpeakSessions
					.Where(s => s.UserId == user.Id && s.DisconnectedAt == null)
					.OrderByDescending(s => s.ConnectedAt)
					.FirstOrDefaultAsync();

				if (session != null)
				{
					session.DisconnectedAt = DateTimeOffset.UtcNow;
					session.FinalTsChannelId = chid;
					session.DisconnectReason = e.ReasonMessage;
				}

				var channel = await db.TeamSpeakChannels.FirstOrDefaultAsync(c => c.TsChannelId == chid);
				if (channel != null && channel.CurrentClientCount > 0)
					channel.CurrentClientCount--;

				await db.SaveChangesAsync();

				var disconnectedPayload = new TsUserDisconnectedEvent
				{
					ClientId = clid,
					UniqueId = user.TsUniqueId,
					Nickname = user.Nickname ?? string.Empty,
					ChannelId = chid,
					Reason = e.ReasonMessage
				};
				Log.Information("Publishing userDisconnected: {@Payload}", disconnectedPayload);
				_ = _publisher.PublishAsync(new Ts3EventEnvelope
				{
					EventType = "userDisconnected",
					Payload = disconnectedPayload
				});
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error handling ClientLeftView for client {ClientId}", e.ClientId);
		}
	}

	/// <summary>
	/// Обрабатывает событие перемещения клиента между каналами в TeamSpeak.
	/// </summary>
	/// <param name="sender">Источник события (обычно клиент TeamSpeak).</param>
	/// <param name="e">Содержит информацию о перемещенном клиенте и целевом канале.</param>
	/// <remarks>
	/// Метод асинхронно обновляет базу данных приложения, чтобы отразить текущее местоположение клиента
	/// в TeamSpeak. Также включена обработка ошибок при взаимодействии с базой данных.
	/// </remarks>
	private async void OnClientMoved(object? sender, ClientMoved e)
	{
		try
		{
			await using var db = await _dbContextFactory.CreateDbContextAsync();
			var clid = (int)e.ClientId.Value;
			var toChid = (int)e.TargetChannelId.Value;

			var user = await db.TeamSpeakUsers.FirstOrDefaultAsync(u => u.CurrentClientId == clid);

			if (user != null)
			{
				var fromChid = user.CurrentTsChannelId;

				user.CurrentTsChannelId = toChid;
				user.UpdatedAt = DateTimeOffset.UtcNow;

				var session = await db.TeamSpeakSessions
					.Where(s => s.UserId == user.Id && s.DisconnectedAt == null)
					.OrderByDescending(s => s.ConnectedAt)
					.FirstOrDefaultAsync();

				db.ChannelMoveLogs.Add(new ChannelMoveLog
				{
					SessionId = session?.Id,
					UserId = user.Id,
					FromTsChannelId = fromChid,
					ToTsChannelId = toChid,
					MovedAt = DateTimeOffset.UtcNow,
					MovedByUid = e.InvokerUid?.Value
				});

				if (fromChid.HasValue)
				{
					var fromChannel =
						await db.TeamSpeakChannels.FirstOrDefaultAsync(c => c.TsChannelId == fromChid.Value);
					if (fromChannel != null && fromChannel.CurrentClientCount > 0)
						fromChannel.CurrentClientCount--;
				}

				var toChannel = await db.TeamSpeakChannels.FirstOrDefaultAsync(c => c.TsChannelId == toChid);
				if (toChannel != null)
					toChannel.CurrentClientCount++;

				await db.SaveChangesAsync();

				var movedPayload = new TsUserMovedEvent
				{
					ClientId = clid,
					UniqueId = user.TsUniqueId,
					Nickname = user.Nickname ?? string.Empty,
					FromChannelId = fromChid,
					ToChannelId = toChid,
					MovedByUid = e.InvokerUid?.Value
				};
				Log.Information("Publishing userMoved: {@Payload}", movedPayload);
				_ = _publisher.PublishAsync(new Ts3EventEnvelope
				{
					EventType = "userMoved",
					Payload = movedPayload
				});
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error handling ClientMoved for client {ClientId}", e.ClientId);
		}
	}

	/// <summary>
	/// Обрабатывает событие обновления данных клиента TeamSpeak (mute/unmute, ник и т.д.).
	/// </summary>
	/// <param name="sender">Источник события.</param>
	/// <param name="e">Данные события <see cref="ClientUpdated"/>.</param>
	private async void OnClientUpdated(object? sender, ClientUpdated e)
	{
		try
		{
			// Обрабатываем только изменения mute-статуса
			if (!e.InputMuted.HasValue && !e.OutputMuted.HasValue)
				return;

			await using var db = await _dbContextFactory.CreateDbContextAsync();
			var clid = (int)e.ClientId.Value;

			var user = await db.TeamSpeakUsers.FirstOrDefaultAsync(u => u.CurrentClientId == clid);
			if (user == null) return;

			var newInputMuted = e.InputMuted ?? user.IsInputMuted;
			var newOutputMuted = e.OutputMuted ?? user.IsOutputMuted;

			// Если ничего не изменилось — не пишем в лог
			if (newInputMuted == user.IsInputMuted && newOutputMuted == user.IsOutputMuted)
				return;

			user.IsInputMuted = newInputMuted;
			user.IsOutputMuted = newOutputMuted;
			user.UpdatedAt = DateTimeOffset.UtcNow;

			var session = await db.TeamSpeakSessions
				.Where(s => s.UserId == user.Id && s.DisconnectedAt == null)
				.OrderByDescending(s => s.ConnectedAt)
				.FirstOrDefaultAsync();

			db.UserAudioStateLogs.Add(new UserAudioStateLog
			{
				UserId = user.Id,
				SessionId = session?.Id,
				IsInputMuted = newInputMuted,
				IsOutputMuted = newOutputMuted,
				ChangedAt = DateTimeOffset.UtcNow
			});

			await db.SaveChangesAsync();

			var audioPayload = new TsUserAudioStateChangedEvent
			{
				ClientId = clid,
				UniqueId = user.TsUniqueId,
				Nickname = user.Nickname ?? string.Empty,
				IsInputMuted = newInputMuted,
				IsOutputMuted = newOutputMuted
			};
			Log.Information("Publishing userAudioStateChanged: {@Payload}", audioPayload);
			_ = _publisher.PublishAsync(new Ts3EventEnvelope
			{
				EventType = "userAudioStateChanged",
				Payload = audioPayload
			});
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error handling ClientUpdated for client {ClientId}", e.ClientId);
		}
	}
}
