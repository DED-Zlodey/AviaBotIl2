using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using TSLib;
using TSLib.Full;
using TSLib.Helper;
using TSLib.Scheduler;
using AviaBot.Models;

namespace AviaBot.Services;

public class Ts3BotService : IHostedService, IDisposable
{
	/// <summary>
	/// Логгер для записи сообщений, связанных с выполнением логики сервиса <c>Ts3BotService</c>.
	/// Используется для документирования событий, ошибок и информации о процессе работы.
	/// </summary>
	private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<Ts3BotService>();

	/// <summary>
	/// Конфигурационные параметры для настройки подключения к серверу TeamSpeak.
	/// Обеспечивает загрузку настроек, таких как адрес сервера, имя пользователя,
	/// пароль сервера и канала, а также уровень безопасности подключения.
	/// </summary>
	private readonly Ts3Settings _settings;

	/// <summary>
	/// Сервис для управления и обработки позиций игроков в игровой сессии.
	/// Используется для получения, обновления и обработки данных о местоположении игроков,
	/// включая их состояние (лобби, в игре) и межигровую коммуникацию.
	/// </summary>
	private readonly PlayerPositionService _positionService;

	/// <summary>
	/// Настройки для управления системой ретрансляции голоса.
	/// Используются для конфигурации параметров передачи голосовых данных
	/// и их взаимодействия с сервисами, такими как <c>VoiceRelayPipe</c>.
	/// </summary>
	private readonly RelaySettings _relaySettings;

	/// <summary>
	/// Настройки записи голосовых данных, используемые в процессе работы бота.
	/// Параметры включают в себя управление включением/выключением записи,
	/// указание пути для сохранения записей и настройку максимального времени
	/// простоя потока речи до завершения записи.
	/// </summary>
	private readonly RecordingSettings _recordingSettings;
	private readonly VoicePlaybackTestService _voicePlaybackTestService;
	private readonly SyntheticLoadSettings _syntheticLoadSettings;
	private readonly TestVoicePlaybackSettings _testSettings;

	/// <summary>
	/// Планировщик задач, работающий в выделенном потоке.
	/// Используется для выполнения асинхронных операций в контексте
	/// TS3-бота, таких как обработка событий и выполнение таймеров.
	/// Обеспечивает синхронный доступ к выполняемым задачам и предотвращает
	/// их конкуренцию в многопоточной среде.
	/// </summary>
	private DedicatedTaskScheduler? _scheduler;

	/// <summary>
	/// Экземпляр клиента TeamSpeak, используемый для взаимодействия с сервером.
	/// Позволяет выполнять различные операции, связанные с сервером TeamSpeak,
	/// такие как отправка сообщений и управление каналами.
	/// Хранит текущее состояние подключения к серверу.
	/// </summary>
	private TsFullClient? _tsFullClient;

	/// <summary>
	/// Переменная, хранящая объект идентификации пользователя, используемый для подключения к серверу TeamSpeak.
	/// Содержит приватный и публичный ключи, а также дополнительные параметры, необходимые для аутентификации
	/// и обеспечения безопасности соединения.
	/// </summary>
	private IdentityData? _identity;

	/// <summary>
	/// Источник токенов отмены для управления процессами в сервисе <c>Ts3BotService</c>.
	/// Используется для координации завершения задач и корректной отмены их выполнения.
	/// </summary>
	private CancellationTokenSource? _cts;

	/// <summary>
	/// Свойство, указывающее текущее состояние подключения к серверу TeamSpeak.
	/// Если значение <c>true</c>, то установленное соединение активно; если <c>false</c>, то соединение отсутствует.
	/// Использует внутренний объект <c>TsFullClient</c> для определения статуса подключения.
	/// </summary>
	public bool IsConnected => _tsFullClient?.Connected ?? false;

	/// <summary>
	/// Клиент TeamSpeak, используемый для взаимодействия с функциональностью TeamSpeak Server в рамках сервиса <c>Ts3BotService</c>.
	/// Предоставляет доступ к методам и событиям для работы с подключением и управления сервером.
	/// </summary>
	public TsFullClient? Client => _tsFullClient;

	/// <summary>
	/// Сервис для работы с TS3 сервером с использованием AviaBot.
	/// Управляет подключением, отправкой сообщений и обработкой событий.
	/// </summary>
	private SyntheticVoiceInjector? _syntheticInjector;

	public Ts3BotService(
		IOptions<Ts3Settings> settings,
		PlayerPositionService positionService,
		RelaySettings relaySettings,
		IOptions<RecordingSettings> recordingSettings,
		VoicePlaybackTestService voicePlaybackTestService,
		IOptions<SyntheticLoadSettings> syntheticLoadSettings,
		IOptions<TestVoicePlaybackSettings> testSettings)
	{
		_settings = settings.Value;
		_positionService = positionService;
		_relaySettings = relaySettings;
		_recordingSettings = recordingSettings.Value;
		_voicePlaybackTestService = voicePlaybackTestService;
		_syntheticLoadSettings = syntheticLoadSettings.Value;
		_testSettings = testSettings.Value;
	}

	/// <summary>
	/// Асинхронно запускает Ts3BotService и задает необходимые процессы для его работы.
	/// </summary>
	/// <param name="cancellationToken">
	/// Токен отмены, который позволяет отменить процесс запуска.
	/// </param>
	/// <returns>
	/// Задача, представляющая асинхронный процесс запуска услуги.
	/// </returns>
	public Task StartAsync(CancellationToken cancellationToken)
	{
		Log.Information("Starting Ts3BotService...");

		try
		{
			var proc = Process.GetCurrentProcess();
			proc.PriorityClass = ProcessPriorityClass.AboveNormal;
			Log.Information("Process priority elevated to {Priority}", proc.PriorityClass);
		}
		catch (Exception ex)
		{
			Log.Warning(ex, "Failed to elevate process priority");
		}

		_cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		// Запускаем DedicatedTaskScheduler в отдельном потоке
		_scheduler = new DedicatedTaskScheduler(new Id(0));
		_scheduler.Invoke(() => RunBotAsync(_cts.Token));

		return Task.CompletedTask;
	}

	/// <summary>
	/// Асинхронно останавливает сервис для работы с сервером TS3.
	/// Выполняет отключение клиента TS3, завершение задач и освобождение ресурсов.
	/// </summary>
	/// <param name="cancellationToken">Токен для отмены операции.</param>
	/// <returns>Задача, представляющая асинхронную операцию остановки сервиса.</returns>
	public async Task StopAsync(CancellationToken cancellationToken)
	{
		Log.Information("Stopping Ts3BotService...");
		await _cts?.CancelAsync()!;

		_voicePlaybackTestService.StopPlayback();

		if (_scheduler != null)
		{
			await _scheduler.InvokeAsync(async () =>
			{
				if (_tsFullClient != null)
				{
					await _tsFullClient.Disconnect();
					_tsFullClient.Dispose();
				}
			});
			_scheduler.Dispose();
		}
	}

	/// <summary>
	/// Асинхронно запускает бот для работы с сервером TS3.
	/// Управляет подключением, авторизацией, обработкой сообщений и другими аспектами работы бота.
	/// </summary>
	/// <param name="ct">Токен отмены, обеспечивающий возможность корректного завершения работы метода.</param>
	/// <returns>Задача, представляющая выполнение асинхронной операции запуска бота.</returns>
	private async Task RunBotAsync(CancellationToken ct)
	{
		try
		{
			_tsFullClient = new TsFullClient(_scheduler);
			_tsFullClient.OnDisconnected += (_, e) =>
			{
				Log.Warning("Disconnected: {0}", e.Error?.ErrorFormat() ?? e.ExitReason.ToString());
			};
			_tsFullClient.OnEachTextMessage += async (_, msg) =>
			{
				Log.Information("TextMessage from {0}: {1}", msg.InvokerName, msg.Message);
				await Task.CompletedTask;
			};

			// Identity
			var identityPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory!, "identity.json");
			if (File.Exists(identityPath))
			{
				var json = await File.ReadAllTextAsync(identityPath);
				var saved = JsonConvert.DeserializeObject<SavedIdentity>(json);
				if (saved?.PrivateKey != null)
				{
					var result = TsCrypt.LoadIdentityDynamic(saved.PrivateKey, saved.Offset);
					if (result.Ok)
					{
						_identity = result.Value;
						Log.Information("Loaded existing identity");
					}
					else
					{
						Log.Warning("Failed to load identity, generating new one");
					}
				}
			}

			if (_identity == null)
			{
				_identity = TsCrypt.GenerateNewIdentity();
				var saved = new SavedIdentity
				{
					PrivateKey = _identity.PrivateKeyString,
					Offset = _identity.ValidKeyOffset
				};
				await File.WriteAllTextAsync(identityPath, JsonConvert.SerializeObject(saved, Formatting.Indented));
				Log.Information("Generated and saved new identity");
			}

			// Security level
			if (_settings.SecurityLevel >= 0 && _settings.SecurityLevel <= 160)
			{
				if (TsCrypt.GetSecurityLevel(_identity) < _settings.SecurityLevel)
				{
					Log.Information("Improving security level to {0}...", _settings.SecurityLevel);
					TsCrypt.ImproveSecurity(_identity, _settings.SecurityLevel);
					var saved = new SavedIdentity
					{
						PrivateKey = _identity.PrivateKeyString,
						Offset = _identity.ValidKeyOffset
					};
					await File.WriteAllTextAsync(identityPath, JsonConvert.SerializeObject(saved, Formatting.Indented));
				}
			}

			// Version
			var versionSign = Tools.IsLinux ? TsVersionSigned.VER_LIN_3_X_X : TsVersionSigned.VER_WIN_3_X_X;

			var connectionData = new ConnectionDataFull(
				_settings.Address,
				_identity,
				versionSign: versionSign,
				username: _settings.Name,
				serverPassword: _settings.ServerPassword,
				defaultChannel: _settings.Channel,
				defaultChannelPassword: _settings.ChannelPassword,
				logId: new Id(0)
			);

			Log.Information("Connecting to {0} as '{1}'...", _settings.Address, _settings.Name);
			var connectResult = await _scheduler!.InvokeAsync(() => _tsFullClient.Connect(connectionData));
			if (!connectResult.GetOk(out var error))
			{
				Log.Error("Could not connect: {0}", error.ErrorFormat());
				return;
			}

			Log.Information("Connected successfully!");

			// Запускаем тестовое воспроизведение MP3, если включено (и не используется новый тестовый режим ретрансляции)
			if (!_testSettings.Enabled)
				_voicePlaybackTestService.StartPlayback(_tsFullClient);

			// Устанавливаем VoiceRelayPipe (в потоке scheduler)
			VoiceRelayPipe? voiceRelayPipe = null;
			await _scheduler.InvokeAsync(() =>
			{
				voiceRelayPipe = new VoiceRelayPipe(
					_tsFullClient,
					_positionService,
					_relaySettings,
					_testSettings);
				_tsFullClient.OutStream = voiceRelayPipe;
				return Task.CompletedTask;
			});

			// Запускаем синтетическую нагрузку, если включено
			if (_syntheticLoadSettings.Enabled && voiceRelayPipe != null)
			{
				_syntheticInjector = new SyntheticVoiceInjector(
					voiceRelayPipe,
					_tsFullClient,
					_positionService,
					_syntheticLoadSettings.SpeakerCount,
					enabled: true);
				_syntheticInjector.Start();
			}

			// Ждём отмены (синхронно в потоке scheduler, чтобы не терять контекст)
			while (!ct.IsCancellationRequested)
			{
				try { await Task.Delay(1000, ct); }
				catch (OperationCanceledException) { break; }
			}
		}
		catch (OperationCanceledException)
		{
			Log.Information("Bot loop cancelled");
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Bot loop error");
		}
	}

	/// <summary>
	/// Асинхронно отправляет личное сообщение пользователю TeamSpeak3 по UID.
	/// Используется для получения клиента по UID и отправки ему сообщения.
	/// </summary>
	/// <param name="uid">UID целевого пользователя TeamSpeak3.</param>
	/// <param name="message">Текст сообщения, которое нужно отправить.</param>
	/// <returns>Возвращает значение true, если сообщение отправлено успешно; иначе false.</returns>
	public async Task<bool> SendPrivateMessageByUidAsync(string uid, string message)
	{
		if (_tsFullClient == null || !_tsFullClient.Connected)
		{
			Log.Warning("Cannot send message: not connected");
			return false;
		}

		try
		{
			// Ищем клиента по UID через ClientList
			var result = await _tsFullClient.ClientList(ClientListOptions.uid);
			if (!result.Ok)
			{
				Log.Warning("ClientList failed: {0}", result.Error.ErrorFormat());
				return false;
			}

			var client = result.Value.FirstOrDefault(c => c.Uid?.ToString() == uid);
			if (client == null)
			{
				Log.Warning("Client with UID {0} not found", uid);
				return false;
			}

			var sendResult = await _tsFullClient.SendPrivateMessage(message, client.ClientId);
			if (!sendResult.Ok)
			{
				Log.Warning("SendPrivateMessage failed: {0}", sendResult.Error.ErrorFormat());
				return false;
			}

			return true;
		}
		catch (Exception ex)
		{
			Log.Error(ex, "SendPrivateMessageByUid failed");
			return false;
		}
	}

	/// <summary>
	/// Отправляет текстовое сообщение в текущий канал, используя соединение с TS3 сервером.
	/// </summary>
	/// <param name="message">Текст сообщения, которое необходимо отправить в канал.</param>
	/// <returns>
	/// Возвращает значение типа <see cref="Task{TResult}"/>, указывающее успех или неуспех операции.
	/// True, если сообщение было успешно отправлено; False, если отправка не удалась.
	/// </returns>
	public async Task<bool> SendChannelMessageAsync(string message)
	{
		if (_tsFullClient == null || !_tsFullClient.Connected)
			return false;

		var result = await _tsFullClient.SendChannelMessage(message);
		return result.Ok;
	}

	public void Dispose()
	{
		_syntheticInjector?.Dispose();
		_cts?.Dispose();
		_tsFullClient?.Dispose();
		_scheduler?.Dispose();
		_voicePlaybackTestService.Dispose();
	}

	/// <summary>
	/// Класс, представляющий сохраненную идентичность пользователя.
	/// Используется для хранения информации, связанной с идентификацией или авторизацией.
	/// </summary>
	private class SavedIdentity
	{
		/// <summary>
		/// Приватный ключ, используемый для идентификации клиента в системе.
		/// Значение сохраняется в формате строки и используется библиотекой TsCrypt
		/// для загрузки или создания нового объекта <c>IdentityData</c>.
		/// </summary>
		public string PrivateKey { get; set; } = "";

		/// <summary>
		/// Смещение ключа безопасности, используемое для обработки и хранения данных идентичности.
		/// </summary>
		/// <remarks>
		/// Данное свойство хранит значение смещения ключа, применяемое при генерации или загрузке данных идентичности.
		/// Оно обеспечивает корректное функционирование криптографических операций.
		/// </remarks>
		public ulong Offset { get; set; }
	}
}
