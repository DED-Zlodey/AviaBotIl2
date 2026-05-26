using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using TSLib;
using TSLib.Audio.Opus;
using TSLib.Full;
using TSLib.Helper;
using TSLib.Scheduler;
using Serilog;

namespace AviaBot.TestUser;

/// <summary>
/// Главный класс программы.
/// Служит точкой входа в приложение и выполняет инициализацию необходимых компонентов.
/// </summary>
static class Program
{
	/// <summary>
	/// Устанавливает минимальный интервал времени для планировщика системы.
	/// Используется для увеличения точности таймеров.
	/// </summary>
	/// <param name="uPeriod">Минимальный интервал времени в миллисекундах.</param>
	/// <returns>Возвращает код ошибки в виде целого числа. Ноль указывает на успешное выполнение.</returns>
	[DllImport("winmm.dll")]
	static extern uint TimeBeginPeriod(uint uPeriod);

	/// <summary>
	/// Завершает высокоточную настройку временного периода для системного таймера.
	/// Этот метод используется для возврата таймера в исходный режим работы.
	/// </summary>
	/// <param name="uPeriod">Период времени, ранее установленный методом TimeBeginPeriod.</param>
	/// <returns>Возвращает код результата выполнения операции, где 0 указывает на успешное выполнение.</returns>
	[DllImport("winmm.dll")]
	static extern uint TimeEndPeriod(uint uPeriod);

	/// <summary>
	/// Точка входа в приложение.
	/// Выполняет инициализацию компонентов, загрузку конфигурации и основной цикл работы приложения.
	/// </summary>
	/// <param name="args">Массив аргументов командной строки, переданных приложению при запуске.</param>
	/// <returns>Возвращает задачу, представляющую асинхронное выполнение метода.</returns>
	static async Task Main(string[] args)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			TimeBeginPeriod(1);

		SetupLogger();

		try
		{
			bool loaded = NativeMethods.PreloadLibrary();
			if (!loaded)
				Log.Warning("Couldn't find libopus. Make sure it is installed or placed in the correct folder.");
			else
				Log.Information("libopus loaded: {OpusInfo}", NativeMethods.Info);

			var config = new ConfigurationBuilder()
				.SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
				.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
				.Build();

			var ts3Settings = new Ts3Settings();
			config.GetSection("Ts3").Bind(ts3Settings);

			var testSettings = new TestSettings();
			config.GetSection("Test").Bind(testSettings);

			if (string.IsNullOrEmpty(testSettings.Name))
			{
				Log.Error("Bot Name is not configured in appsettings.json");
				Console.WriteLine("Press any key to exit...");
				Console.ReadKey();
				return;
			}

			using var runner = new TestUserRunner(ts3Settings, testSettings);
			bool connected = await runner.InitializeAsync();
			if (!connected)
			{
				Log.Error("Failed to connect bot {Name}", testSettings.Name);
				Console.WriteLine("Press any key to exit...");
				Console.ReadKey();
				return;
			}

			Log.Information("=== Bot '{BotName}' ready ===", testSettings.Name);
			Log.Information("=== Press SPACE to start/replay voice ===");
			Log.Information("=== Press ESC to exit ===");

			while (true)
			{
				if (Console.KeyAvailable)
				{
					var key = Console.ReadKey(intercept: true);
					if (key.Key == ConsoleKey.Spacebar)
					{
						runner.TogglePlayback();
					}
					else if (key.Key == ConsoleKey.Escape)
					{
						break;
					}
				}
				await Task.Delay(50);
			}
		}
		catch (Exception ex)
		{
			Log.Fatal(ex, "Fatal error");
		}
		finally
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				TimeEndPeriod(1);
			await Log.CloseAndFlushAsync();
		}
	}

	/// <summary>
	/// Метод, предназначенный для настройки системы логирования приложения.
	/// Конфигурирует уровень логов и настройку их отображения в консоли.
	/// </summary>
	static void SetupLogger()
	{
		Log.Logger = new LoggerConfiguration()
			.MinimumLevel.Information()
			.MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
			.WriteTo.Console(
				outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
			.CreateLogger();
	}
}

/// <summary>
/// Класс Ts3Settings содержит настройки подключения к серверу TeamSpeak 3.
/// Включает параметры, такие как адрес сервера, пароль сервера, канал, пароль канала и уровень безопасности.
/// Эти настройки необходимы для установления успешного соединения с сервером.
/// </summary>
public class Ts3Settings
{
	/// <summary>
	/// Адрес сервера TeamSpeak 3. Это свойство используется для указания
	/// расположения сервера, к которому необходимо подключиться. Формат адреса
	/// может включать доменное имя или IP-адрес вместе с портом через двоеточие.
	/// </summary>
	public string Address { get; set; } = "localhost:9987";

	/// <summary>
	/// Пароль для подключения к серверу. Это свойство используется для указания
	/// пароля, необходимого для авторизации при подключении к серверу TeamSpeak
	/// или подобного сервиса.
	/// </summary>
	public string ServerPassword { get; set; } = "";

	/// <summary>
	/// Имя канала, к которому необходимо подключиться на сервере TeamSpeak 3.
	/// Это свойство используется для указания канала, куда будет направлен клиент
	/// после успешного подключения.
	/// </summary>
	public string Channel { get; set; } = "";

	/// <summary>
	/// Пароль канала, используемый для подключения к указанному каналу на сервере.
	/// Это свойство позволяет указать защищённый доступ к каналу, требующему авторизации.
	/// </summary>
	public string ChannelPassword { get; set; } = "";

	/// <summary>
	/// Уровень безопасности, определяющий минимальную сложность ключа идентификации,
	/// который должен быть использован для подключения. Указывается как число в диапазоне
	/// от 0 до 160, где большее значение соответствует более высокому уровню защиты.
	/// Это свойство позволяет усиливать защиту сессии, если текущий ключ не соответствует
	/// заданным требованиям безопасности.
	/// </summary>
	public int SecurityLevel { get; set; } = -1;
}

/// <summary>
/// Класс, содержащий настройки для выполнения тестового сценария.
/// Включает параметры, такие как целевой никнейм, имя текущего пользователя
/// и путь к аудиофайлу, используемому при воспроизведении.
/// </summary>
public class TestSettings
{
	/// <summary>
	/// Никнейм целевого пользователя, для которого будет выполнено взаимодействие
	/// в процессе работы приложения. Это свойство позволяет указать имя
	/// клиента, с которым необходимо установить связь или выполнить определённые действия.
	/// </summary>
	public string TargetNickname { get; set; } = "Dispatcher";

	/// <summary>
	/// Имя бота, которое используется для идентификации и отображения в различных системах.
	/// Данный параметр загружается из конфигурационного файла и должен быть задан перед началом работы.
	/// Если имя не указано, запуск бота не будет выполнен.
	/// </summary>
	public string Name { get; set; } = "TestPilot1";

	/// <summary>
	/// Путь к файлу, связанный с настройками тестового пользователя.
	/// Значение используется для указания конкретного файла, такого как аудиофайл или конфигурационный файл,
	/// необходимого для работы тестового процесса.
	/// </summary>
	public string FilePath { get; set; } = "test1.mp3";
}

/// <summary>
/// Класс, предназначенный для управления пользователем в тестовой среде TeamSpeak 3.
/// Инкапсулирует логику инициализации, подключения и воспроизведения аудио.
/// </summary>
public class TestUserRunner : IDisposable
{
	/// <summary>
	/// Логгер, используемый для записи сообщений журнала, таких как информация, предупреждения и ошибки,
	/// происходящие в процессе работы класса TestUserRunner.
	/// Это поле позволяет фиксировать различные события и исключения для последующего анализа.
	/// </summary>
	private static readonly ILogger Log = Serilog.Log.ForContext<TestUserRunner>();

	/// <summary>
	/// Настройки для подключения к серверу TeamSpeak 3, содержащие информацию, такую как адрес сервера, пароль сервера,
	/// название канала, пароль канала и уровень безопасности. Эти настройки используются для создания и настройки соединения
	/// с сервером.
	/// </summary>
	private readonly Ts3Settings _ts3Settings;

	/// <summary>
	/// Настройки для выполнения тестового пользователя.
	/// Включают информацию о целевом никнейме, имени пользователя и пути к аудиофайлу,
	/// использующемуся при воспроизведении.
	/// </summary>
	private readonly TestSettings _testSettings;

	/// <summary>
	/// Переменная, представляющая экземпляр бота, используемого для подключения и взаимодействия с сервером.
	/// Хранит настройки и ссылку на объект, который управляет клиентом и выполняет задачи,
	/// связанные с воспроизведением и другими операциями.
	/// </summary>
	private BotInstance? _bot;

	/// <summary>
	/// Источник токена отмены, используемый для управления задачами воспроизведения в рамках работы класса TestUserRunner.
	/// Позволяет сигнализировать об отмене операций, таких как воспроизведение, и управлять их завершением.
	/// Это поле освобождается при остановке воспроизведения или завершении работы.
	/// </summary>
	private CancellationTokenSource? _cts;

	/// <summary>
	/// Асинхронная задача, ответственная за воспроизведение аудио.
	/// Используется для управления процессом запуска и остановки воспроизведения
	/// в соответствии с текущими настройками и состоянием объекта TestUserRunner.
	/// </summary>
	private Task? _playbackTask;

	/// <summary>
	/// Объект блокировки, используемый для синхронизации потоков при доступе к разделяемым ресурсам класса TestUserRunner.
	/// Гарантирует, что только один поток одновременно может выполнять блок кода, защищенный этим объектом.
	/// </summary>
	private readonly Lock _lock = new();

	/// <summary>
	/// Класс, предназначенный для управления пользователем в тестовой среде TeamSpeak 3.
	/// Инкапсулирует логику инициализации, подключения и воспроизведения аудио.
	/// </summary>
	public TestUserRunner(Ts3Settings ts3Settings, TestSettings testSettings)
	{
		_ts3Settings = ts3Settings;
		_testSettings = testSettings;
	}

	/// <summary>
	/// Выполняет асинхронную инициализацию бота, включая его подключение и настройку.
	/// В случае успешного выполнения возвращает информацию о состоянии подключения.
	/// </summary>
	/// <returns>Значение true, если инициализация и подключение прошли успешно, иначе false.</returns>
	public async Task<bool> InitializeAsync()
	{
		Log.Information("Connecting bot '{Name}'...", _testSettings.Name);

		try
		{
			_bot = await CreateAndConnectBotAsync(_testSettings);
			return _bot != null;
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Failed to connect bot {Name}", _testSettings.Name);
			return false;
		}
	}

	/// <summary>
	/// Создаёт экземпляр бота, выполняет его настройку и подключает к серверу.
	/// </summary>
	/// <param name="config">Конфигурация бота, содержащая имя и дополнительные параметры.</param>
	/// <returns>Возвращает экземпляр <c>BotInstance</c> при успешном подключении или <c>null</c>, если подключение не удалось.</returns>
	private async Task<BotInstance?> CreateAndConnectBotAsync(TestSettings config)
	{
		var scheduler = new DedicatedTaskScheduler(new Id(0));
		var client = new TsFullClient(scheduler);

		client.OnDisconnected += (_, e) =>
		{
			Log.Warning("Bot {Name} disconnected: {Reason}", config.Name,
				e.Error?.ErrorFormat() ?? e.ExitReason.ToString());
		};

		var identityPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory!, $"identity_{config.Name}.json");
		IdentityData? identity = null;

		if (File.Exists(identityPath))
		{
			var json = await File.ReadAllTextAsync(identityPath);
			var saved = JsonConvert.DeserializeObject<SavedIdentity>(json);
			if (saved?.PrivateKey != null)
			{
				var result = TsCrypt.LoadIdentityDynamic(saved.PrivateKey, saved.Offset);
				if (result.Ok)
					identity = result.Value;
			}
		}

		if (identity == null)
		{
			identity = TsCrypt.GenerateNewIdentity();
			var saved = new SavedIdentity
			{
				PrivateKey = identity.PrivateKeyString,
				Offset = identity.ValidKeyOffset
			};
			await File.WriteAllTextAsync(identityPath, JsonConvert.SerializeObject(saved, Formatting.Indented));
		}

		if (_ts3Settings.SecurityLevel >= 0 && _ts3Settings.SecurityLevel <= 160)
		{
			if (TsCrypt.GetSecurityLevel(identity) < _ts3Settings.SecurityLevel)
				TsCrypt.ImproveSecurity(identity, _ts3Settings.SecurityLevel);
		}

		var versionSign = Tools.IsLinux ? TsVersionSigned.VER_LIN_3_X_X : TsVersionSigned.VER_WIN_3_X_X;

		var connectionData = new ConnectionDataFull(
			_ts3Settings.Address,
			identity,
			versionSign: versionSign,
			username: config.Name,
			serverPassword: _ts3Settings.ServerPassword,
			defaultChannel: _ts3Settings.Channel,
			defaultChannelPassword: _ts3Settings.ChannelPassword,
			logId: new Id(0)
		);

		var connectResult = await scheduler.InvokeAsync(() => client.Connect(connectionData));
		if (!connectResult.GetOk(out var error))
		{
			Log.Error("Bot {Name} could not connect: {Error}", config.Name, error.ErrorFormat());
			scheduler.Dispose();
			client.Dispose();
			return null;
		}

		Log.Information("Bot {Name} connected to {Address}", config.Name, _ts3Settings.Address);
		return new BotInstance { Name = config.Name, FilePath = config.FilePath, Client = client, Scheduler = scheduler };
	}

	/// <summary>
	/// Переключает состояние воспроизведения аудио.
	/// Если в данный момент воспроизведение активно, оно будет остановлено и перезапущено.
	/// Если воспроизведение неактивно, оно будет запущено.
	/// </summary>
	public void TogglePlayback()
	{
		lock (_lock)
		{
			if (_cts != null)
			{
				Log.Information("Stopping playback for replay...");
				_cts.Cancel();
				_cts.Dispose();
				_cts = null;

				Task.Run(async () =>
				{
					if (_playbackTask != null)
						await Task.WhenAny(_playbackTask, Task.Delay(2000));
					StartPlaybackCore();
				});
			}
			else
			{
				StartPlaybackCore();
			}
		}
	}

	/// <summary>
	/// Запускает процесс воспроизведения аудиофайла.
	/// Инициирует выполнение задачи по воспроизведению для целевого пользователя
	/// в соответствии с указанными настройками.
	/// </summary>
	private void StartPlaybackCore()
	{
		lock (_lock)
		{
			if (_cts != null) return;
			if (_bot == null)
			{
				Log.Warning("Bot not connected");
				return;
			}

			_cts = new CancellationTokenSource();
			var token = _cts.Token;
			_playbackTask = Task.Run(() => RunPlaybackAsync(token), token);
		}
	}

	/// <summary>
	/// Выполняет воспроизведение аудиофайла для целевого пользователя в TeamSpeak 3.
	/// </summary>
	/// <param name="ct">Токен отмены, позволяющий прервать операцию воспроизведения.</param>
	/// <returns>Возвращает задачу, представляющую асинхронную операцию. При успешном завершении возвращает void.</returns>
	private async Task RunPlaybackAsync(CancellationToken ct)
	{
		try
		{
			if (_bot == null) return;

			var targetId = await FindTargetClientIdAsync(ct);
			if (targetId == default)
			{
				Log.Warning("Target '{Nickname}' not found on TS3 server", _testSettings.TargetNickname);
				return;
			}

			var mp3Path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory!, _bot.FilePath);
			if (!File.Exists(mp3Path))
			{
				Log.Warning("MP3 file not found: {Path}", mp3Path);
				return;
			}

			var pcm = await DecodeMp3Async(mp3Path, ct);
			if (pcm == null)
			{
				Log.Warning("Failed to decode MP3");
				return;
			}

			var frames = SplitIntoFrames(pcm);
			Log.Information("Starting playback to {Nickname} ({ClientId}), {Frames} frames",
				_testSettings.TargetNickname, targetId, frames.Length);

			await RunBotPlaybackAsync(_bot, frames, targetId, ct);
			Log.Information("Playback completed");
		}
		catch (OperationCanceledException)
		{
			Log.Information("Playback cancelled");
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Playback error");
		}
		finally
		{
			lock (_lock)
			{
				_cts?.Dispose();
				_cts = null;
			}
		}
	}

	/// <summary>
	/// Воспроизводит аудио с использованием указанного бота, отправляя аудиофреймы целевому клиенту.
	/// </summary>
	/// <param name="bot">Экземпляр бота, через которого будет осуществляться воспроизведение.</param>
	/// <param name="frames">Массив аудиофреймов, которые необходимо воспроизвести.</param>
	/// <param name="targetId">Идентификатор целевого клиента, которому будет отправлено аудио.</param>
	/// <param name="ct">Токен отмены для остановки воспроизведения.</param>
	/// <returns>Задача, представляющая асинхронное выполнение метода.</returns>
	private async Task RunBotPlaybackAsync(BotInstance bot, byte[][] frames, ClientId targetId, CancellationToken ct)
	{
		using var encoder = OpusEncoder.Create(48000, 1, Application.Voip);
		var encodeBuffer = new byte[4096];
		var recipientList = new[] { targetId };

		var sw = Stopwatch.StartNew();
		long startMs = sw.ElapsedMilliseconds;
		int tick = 0;

		Log.Information("Bot {Name} playing {Frames} frames", bot.Name, frames.Length);

		while (!ct.IsCancellationRequested && tick < frames.Length)
		{
			long targetMs = startMs + tick * 20L;
			long now = sw.ElapsedMilliseconds;
			int sleep = (int)(targetMs - now);
			if (sleep > 0)
			{
				try { await Task.Delay(sleep, ct); }
				catch (OperationCanceledException) { break; }
			}

			var frame = frames[tick];
			var encoded = encoder.Encode(frame.AsSpan(), encodeBuffer.Length, encodeBuffer.AsSpan());

			var packetId = bot.Client.AllocateVoiceWhisperId();
			bot.Client.SendAudioWhisper(
				encoded,
				Codec.OpusVoice,
				Array.Empty<ChannelId>(),
				recipientList,
				packetId);

			tick++;

			if (tick % 250 == 0)
			{
				Log.Debug("Bot {Name} {Tick}/{Total}", bot.Name, tick, frames.Length);
			}
		}

		Log.Information("Bot {Name} finished", bot.Name);
	}

	/// <summary>
	/// Выполняет поиск идентификатора клиента на основе заданного псевдонима цели.
	/// </summary>
	/// <param name="ct">Токен отмены для прерывания операции поиска.</param>
	/// <returns>Возвращает идентификатор клиента типа <see cref="ClientId"/> или значение по умолчанию, если клиент не найден.</returns>
	private async Task<ClientId> FindTargetClientIdAsync(CancellationToken ct)
	{
		if (_bot == null) return default;

		for (int i = 0; i < 30; i++)
		{
			if (_bot.Client.Connected)
			{
				foreach (var kvp in _bot.Client.Book.Clients)
				{
					if (string.Equals(kvp.Value.Name, _testSettings.TargetNickname, StringComparison.OrdinalIgnoreCase))
						return kvp.Key;
				}
			}
			try { await Task.Delay(500, ct); }
			catch (OperationCanceledException) { break; }
		}
		return default;
	}

	/// <summary>
	/// Декодирует MP3-файл в формат PCM.
	/// </summary>
	/// <param name="filePath">Путь к MP3-файлу, который необходимо декодировать.</param>
	/// <param name="ct">Токен отмены для прерывания операции.</param>
	/// <returns>Массив байтов, представляющий PCM-данные, или null, если декодирование не удалось.</returns>
	private static async Task<byte[]?> DecodeMp3Async(string filePath, CancellationToken ct)
	{
		var baseDir = AppDomain.CurrentDomain.BaseDirectory!;
		var ffmpegPath = Path.Combine(baseDir, "ffmpeg.exe");
		if (!File.Exists(ffmpegPath))
		{
			Log.Warning("ffmpeg not found at {Path}", ffmpegPath);
			return null;
		}

		if (!File.Exists(filePath))
		{
			Log.Warning("MP3 file not found: {Path}", filePath);
			return null;
		}

		var psi = new ProcessStartInfo
		{
			FileName = ffmpegPath,
			Arguments = $"-hide_banner -loglevel error -i \"{filePath}\" -ar 48000 -ac 1 -f s16le pipe:1",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};

		using var process = Process.Start(psi);
		if (process == null) return null;

		try
		{
			await using var stdout = process.StandardOutput.BaseStream;
			using var ms = new MemoryStream();
			await stdout.CopyToAsync(ms, ct);

			var error = await process.StandardError.ReadToEndAsync(ct);
			await process.WaitForExitAsync(ct);

			if (process.ExitCode != 0)
			{
				Log.Warning("ffmpeg failed for {File}: {Error}", filePath, error);
				return null;
			}

			return ms.ToArray();
		}
		catch (OperationCanceledException)
		{
			process.Kill(entireProcessTree: true);
			throw;
		}
	}

	/// <summary>
	/// Разделяет данные PCM на фреймы фиксированного размера.
	/// Используется для подготовки аудио данных к воспроизведению.
	/// </summary>
	/// <param name="pcm">Массив байт, содержащий декодированные PCM-данные.</param>
	/// <returns>Массив фреймов, каждый из которых представляет часть исходных PCM-данных фиксированной длины.</returns>
	private static byte[][] SplitIntoFrames(byte[] pcm)
	{
		const int frameBytes = 960 * 1 * 2;
		int count = pcm.Length / frameBytes;
		var frames = new byte[count][];
		for (int i = 0; i < count; i++)
		{
			frames[i] = new byte[frameBytes];
			Buffer.BlockCopy(pcm, i * frameBytes, frames[i], 0, frameBytes);
		}
		return frames;
	}

	public void Dispose()
	{
		try { _cts?.Cancel(); }
		catch
		{
			// ignored
		}

		if (_bot != null)
		{
			try
			{
				if (_bot.Client?.Connected == true)
				{
					_bot.Scheduler?.InvokeAsync(() => _bot.Client.Disconnect())
						.Wait(TimeSpan.FromSeconds(2));
				}
			}
			catch
			{
				// ignored
			}

			_bot.Client?.Dispose();
			_bot.Scheduler?.Dispose();
		}

		_cts?.Dispose();
	}

	/// <summary>
	/// Класс, представляющий экземпляр бота.
	/// Содержит свойства для хранения информации о боте, управления клиентом и планировщиком задач.
	/// Используется для управления подключением и взаимодействием с сервером.
	/// </summary>
	private class BotInstance
	{
		/// <summary>
		/// Определяет имя, используемое для идентификации экземпляра или объекта.
		/// Это значение может применяться, например, для обозначения бота
		/// в событиях, логах или настройках соединения.
		/// </summary>
		public string Name { get; set; } = "";
		public string FilePath { get; set; } = "";

		/// <summary>
		/// Предоставляет доступ к клиентскому объекту библиотеки TsFullClient, который
		/// используется для выполнения операций, связанных с подключением, обменом сообщениями,
		/// отправкой аудио и управления событиями клиента.
		/// </summary>
		public TsFullClient Client { get; set; } = null!;

		/// <summary>
		/// Свойство представляет экземпляр планировщика задач, используемого для управления выполнением задач
		/// в одном потоке. Предназначен для обеспечения согласованности и изоляции выполнения задач,
		/// что особенно важно для асинхронных операций или задач, требующих выполнения в одном контексте.
		/// Планировщик задач используется для обеспечения последовательного выполнения задач,
		/// создания таймеров и управления их состоянием (включение/выключение, выполнение по интервалу),
		/// а также выполнения операций синхронно или асинхронно на специализированном потоке.
		/// Рекомендуется использовать это свойство для задач, требующих строгого управления
		/// контекстом выполнения для обеспечения стабильной работы и синхронизации.
		/// </summary>
		public DedicatedTaskScheduler Scheduler { get; set; } = null!;
	}

	/// <summary>
	/// Класс, представляющий сохраненные данные идентичности TeamSpeak 3.
	/// Содержит приватный ключ и смещение, используемое для идентификации клиента.
	/// </summary>
	private class SavedIdentity
	{
		/// <summary>
		/// Приватный ключ, используемый для идентификации и обеспечения безопасности.
		/// Содержит строковое представление ключа, необходимого для создания или загрузки идентичности.
		/// </summary>
		public string PrivateKey { get; set; } = "";

		/// <summary>
		/// Свойство, представляющее смещение (offset), применяемое для управления состоянием или использованием ключа.
		/// Обычно используется при работе с данными идентификации, чтобы указать текущую или последнюю валидную позицию ключа.
		/// </summary>
		public ulong Offset { get; set; }
	}
}
