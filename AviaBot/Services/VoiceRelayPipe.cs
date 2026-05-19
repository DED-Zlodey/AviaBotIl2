using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using AviaBot.Enums;
using AviaBot.Models;
using TSLib;
using TSLib.Audio;
using TSLib.Audio.Opus;
using TSLib.Full;

namespace AviaBot.Services;

/// <summary>
/// Простой PCM-микшер: decode → mix → encode → один whisper всем получателям.
/// Без радио-эффектов, без per-recipient encode. Один поток от бота = один mixed stream.
/// </summary>
public class VoiceRelayPipe : IAudioPassiveConsumer, IDisposable
{
	/// <summary>
	/// Экземпляр логера, предназначенный для записи диагностических сообщений,
	/// связанных с работой класса `VoiceRelayPipe`.
	/// Используется для логирования информации, отладки и обработки ошибок, возникающих
	/// в процессе функционирования аудиомикшера.
	/// </summary>
	private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<VoiceRelayPipe>();

	/// <summary>
	/// Экземпляр клиента, предоставляющий полный функционал для работы с TeamSpeak.
	/// Используется для взаимодействия с сервером, управления клиентами,
	/// обработки аудиоданных и передачи голосовых сообщений.
	/// </summary>
	private readonly TsFullClient _tsClient;

	/// <summary>
	/// Сервис для управления и получения информации о позициях игроков.
	/// Используется для обработки данных о местонахождении игроков,
	/// включая их положение в игровом пространстве, нахождение в лобби или активной игре,
	/// а также определение получателей аудиопотоков в зависимости от их местоположения.
	/// </summary>
	private readonly PlayerPositionService _positionService;

	/// <summary>
	/// Настройки передачи аудиопотока, используемые для работы класса `VoiceRelayPipe`.
	/// Определяют параметры, такие как максимальная дистанция передачи,
	/// проверка на участие в одной коалиции, таймауты речи, а также
	/// включение/отключение лобби и радиопередачи.
	/// </summary>
	private readonly RelaySettings _settings;

	/// <summary>
	/// Экземпляр настроек воспроизведения голосового теста, предназначенный для управления
	/// параметрами тестового режима отправки аудиоданных в классе `VoiceRelayPipe`.
	/// Используется для включения/отключения тестового режима и задания параметров
	/// тестового воспроизведения, таких как целевой пользователь для отправки аудиоданных.
	/// </summary>
	private readonly TestVoicePlaybackSettings _testSettings;

	/// <summary>
	/// Следующий потребитель аудиоданных в цепочке обработки.
	/// Используется для передачи декодированных, обработанных и (при необходимости) преобразованных
	/// аудиоданных следующему компоненту, реализующему интерфейс `IAudioPassiveConsumer`.
	/// Может быть равен null, если цепочка обработки завершается на текущем компоненте.
	/// </summary>
	private IAudioPassiveConsumer? _nextPipe;

	/// <summary>
	/// Частота дискретизации аудиосигнала, используемая в процессе обработки данных
	/// в классе `VoiceRelayPipe`. Определяет количество отсчетов звукового сигнала
	/// в секунду, измеряемое в герцах (Гц). Является ключевым параметром для
	/// кодирования, декодирования и микширования звуковых данных.
	/// </summary>
	private const int SampleRate = 48000;

	/// <summary>
	/// Длительность одного аудиофрейма в миллисекундах.
	/// Используется для синхронизации потоков и вычисления размеров буферов
	/// при обработке аудиоданных в реальном времени.
	/// </summary>
	private const int FrameMs = 20;

	/// <summary>
	/// Количество семплов в одном аудиофрейме для заданной частоты дискретизации и длительности одного кадра.
	/// Вычисляется на основе частоты дискретизации и длительности кадра в миллисекундах.
	/// Используется для определения размеров буферов и обработки аудиоданных в реальном времени.
	/// </summary>
	private const int FrameSamples = SampleRate / 1000 * FrameMs; // 960

	/// <summary>
	/// Буфер для хранения PCM-данных оратора.
	/// Содержит очередь звуковых фреймов (PCM chunks), имя отправителя и отметку времени последней активности.
	/// Используется для обработки и микширования аудиоданных в реальном времени.
	/// </summary>
	private class SpeakerBuffer
	{
		/// <summary>
		/// Очередь для хранения звуковых фреймов (чанков), представляющих сегменты аудиоданных.
		/// Используется для временного буферизования декодированных аудиофреймов,
		/// которые позже обрабатываются в процессе передачи или обработки звука.
		/// </summary>
		public readonly ConcurrentQueue<short[]> Chunks = new();

		/// <summary>
		/// Имя отправителя голосового сообщения.
		/// Используется для идентификации источника звуковых данных,
		/// а также для контроля активности пользователей в аудиопотоке.
		/// </summary>
		public string SenderName = "";

		/// <summary>
		/// Время последнего обнаруженного действия спикера.
		/// Используется для отслеживания активности спикера и выполнения операций
		/// очистки, обработки или исключения устаревших данных. Обновляется при успешной
		/// обработке новых аудиоданных от соответствующего клиента.
		/// </summary>
		public DateTime LastSeen = DateTime.UtcNow;
	}

	/// <summary>
	/// Словарь для хранения данных, связанных с каждым активным спикером
	/// (идентифицированным по ClientId).
	/// Используется для управления буферами и метаданными аудиопотока,
	/// связанными с каждым клиентом.
	/// </summary>
	private readonly ConcurrentDictionary<ClientId, SpeakerBuffer> _speakers = new();

	/// <summary>
	/// Коллекция декодеров Opus, используемая для обработки входящих аудиопотоков.
	/// Ключом является пара клиентского идентификатора клиента (ClientId) и используемой кодировки (Codec).
	/// Значением является объект OpusDecoder, ответственный за декодирование аудиоданных данного клиента в соответствии с кодировкой.
	/// Обеспечивает корректное сопоставление декодеров для различных клиентов и их аудиоформатов.
	/// </summary>
	private readonly ConcurrentDictionary<(ClientId, Codec), OpusDecoder> _decoders = new();

	/// <summary>
	/// Токен для управления отменой операций микширования аудио.
	/// Используется для завершения выполнения потоков микширования
	/// при остановке или освобождении ресурса VoiceRelayPipe.
	/// </summary>
	private readonly CancellationTokenSource _cts = new();

	/// <summary>
	/// Поток, ответственный за выполнение цикла микширования аудиоданных в рамках голосового конвейера.
	/// Выполняет фоновые операции по обработке, смешиванию и кодированию аудиопотоков,
	/// обеспечивая их дальнейшую передачу обработчику.
	/// </summary>
	/// <remarks>
	/// Поток запускается при инициализации объекта VoiceRelayPipe и работает до
	/// завершения работы или освобождения ресурсов, связанных с данным экземпляром.
	/// Имеет приоритет выше среднего для обеспечения низкой задержки обработки аудиоданных.
	/// </remarks>
	private readonly Thread _mixerThread;

	/// <summary>
	/// Экземпляр Opus-кодека, используемый для кодирования аудиопотока в формат Opus.
	/// Выполняет сжатие PCM-данных, преобразуя их в оптимизированный для передачи формат.
	/// Кодек конфигурируется для монофонического VoIP-приложения с фиксированной частотой дискретизации.
	/// </summary>
	private OpusEncoder? _encoder;

	// Радио-шум
	/// <summary>
	/// Локальный генератор случайных чисел, связанный с текущим потоком.
	/// Используется для создания слабозащищенного шума в аудиосигнале,
	/// а также для выполнения других операций, связанных с генерацией случайных значений.
	/// </summary>
	private readonly ThreadLocal<Random> _random = new(() => new Random());

	/// <summary>
	/// Текущее состояние генератора радиошума.
	/// Используется для создания эффектов низкочастотного шума, которые добавляются к основному аудиопотоку.
	/// Комбинируется с изменяющейся амплитудой для придания большей реалистичности.
	/// </summary>
	private double _noiseState;

	/// <summary>
	/// Текущая амплитуда шума.
	/// Используется для генерации розового шума с плавно изменяющейся громкостью,
	/// который добавляется в смешанный аудиопоток для создания эффекта реалистичного звукового окружения.
	/// </summary>
	private double _currentNoiseAmp = 2000.0;

	/// <summary>
	/// Индекс текущего такта обработки звука.
	/// Используется для ведения учета времени внутри алгоритма обработки звуковых данных,
	/// таких как смешивание активных источников звука, добавление радиошума и управление амплитудой.
	/// </summary>
	private long _tickIndex;

	// Кэш ClientId
	/// <summary>
	/// Кэш клиентских идентификаторов (ClientId).
	/// Используется для быстрого доступа к идентификаторам клиентов по их именам,
	/// чтобы избежать необходимости повторного перебора всех клиентов.
	/// </summary>
	private readonly Dictionary<string, ClientId> _clientIdCache = new();

	/// <summary>
	/// Последнее время обновления кэша клиентов.
	/// Используется для предотвращения частых обновлений кэша,
	/// что уменьшает нагрузку на систему при обработке данных клиентов.
	/// </summary>
	private DateTime _lastClientCacheRefresh = DateTime.MinValue;

	/// <summary>
	/// Определяет следующий целевой обработчик аудиопотока.
	/// Свойство позволяет установить или получить объект, реализующий интерфейс <see cref="IAudioPassiveConsumer"/>,
	/// который будет получать обработанные аудиоданные.
	/// </summary>
	public IAudioPassiveConsumer? OutStream { get => _nextPipe; set => _nextPipe = value; }

	/// <summary>
	/// Определяет, активно ли воспроизведение или обработка аудиопотока.
	/// Возвращает значение <c>true</c>, если объект находится в активном состоянии.
	/// </summary>
	public bool Active => true;

	/// <summary>
	/// Голосовой конвейер для обработки аудиопотоков. Выполняет декодирование, микширование
	/// и кодирование аудиоданных, отправляя результат единому получателю.
	/// </summary>
	/// <remarks>
	/// Этот класс обеспечивает простой PCM-микшер, который принимает аудиоданные, смешивает их
	/// между собой и передает целевой обработке. Реализует функциональность без поддержки
	/// дополнительных эффектов, таких как радио-эффекты, а также без индивидуального кодирования
	/// для каждого получателя. Работает по принципу "один поток бота = один смешанный поток".
	/// Запускает управляющий поток для микширования и обработки.
	/// </remarks>
	public VoiceRelayPipe(TsFullClient tsClient, PlayerPositionService positionService, RelaySettings settings,
		TestVoicePlaybackSettings testSettings)
	{
		_tsClient = tsClient;
		_positionService = positionService;
		_settings = settings;
		_testSettings = testSettings;

		_mixerThread = new Thread(MixerLoop)
		{
			IsBackground = true,
			Priority = ThreadPriority.AboveNormal,
			Name = "VoiceRelayMixer"
		};
		_mixerThread.Start();
	}

	/// <summary>
	/// Обрабатывает входные аудиоданные и передает их следующему потребителю,
	/// при необходимости декодируя и фильтруя данные.
	/// Управляет проверкой данных и дальнейшей маршрутизацией.
	/// </summary>
	/// <param name="data">Буфер с аудиоданными для обработки. Ожидается, что данные содержат информацию о кодеке и клиенте.</param>
	/// <param name="meta">Метаинформация о входном аудиопотоке, содержащая флаги и свойства.</param>
	/// <remarks>
	/// Метод выполняет декодирование входного аудиопотока, проверяет принадлежность отправителя
	/// к допустимой коалиции на основе имени и передает обработанный буфер следующему потребителю.
	/// Оптимизирует работу с временными буферами с использованием пулов.
	/// </remarks>
	public void Write(Span<byte> data, Meta? meta)
	{
		_nextPipe?.Write(data, meta);

		if (data.Length < 6) return;

		var realClientId = (ClientId)BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
		var codec = (Codec)data[4];

		if (!_tsClient.Book.Clients.TryGetValue(realClientId, out var client)) return;
		var senderName = client.Name;
		if (string.IsNullOrEmpty(senderName)) return;
		if (!(meta?.In.Whisper ?? false)) return;

		bool isTestMode = _testSettings.Enabled;

		if (!isTestMode)
		{
			if (!_positionService.TryGetByName(senderName, out var sender) || sender == null) return;
			if (sender.Coalition != 101 && sender.Coalition != 201) return;
		}

		var audioData = data.Slice(5);
		if (audioData.Length < 3) return;

		int channels = codec == Codec.OpusVoice ? 1 : 2;
		int decodeBufSize = channels * FrameSamples * 2;

		var decodeBuf = ArrayPool<byte>.Shared.Rent(decodeBufSize);
		var opusTemp = ArrayPool<byte>.Shared.Rent(audioData.Length);

		try
		{
			audioData.CopyTo(opusTemp);

			var decoder = _decoders.GetOrAdd((realClientId, codec), _ =>
				OpusDecoder.Create(SampleRate, channels));

			ReadOnlySpan<byte> decoded;
			try
			{
				decoded = decoder.Decode(
					opusTemp.AsSpan(0, audioData.Length),
					decodeBuf.AsSpan(0, decodeBufSize));
			}
			catch (Exception ex)
			{
				Log.Debug(ex, "VoiceRelay: decode failed for {Name}", senderName);
				return;
			}

			int sampleCount = decoded.Length / 2;
			if (sampleCount == 0) return;

			short[] monoChunk = new short[FrameSamples];
			if (channels == 1)
			{
				int len = Math.Min(FrameSamples, sampleCount);
				for (int i = 0; i < len; i++)
					monoChunk[i] = (short)(decoded[i * 2] | (decoded[i * 2 + 1] << 8));
			}
			else
			{
				int len = Math.Min(FrameSamples, sampleCount / 2);
				for (int i = 0; i < len; i++)
					monoChunk[i] = (short)(decoded[i * 4] | (decoded[i * 4 + 1] << 8));
			}

			var buf = _speakers.GetOrAdd(realClientId, _ => new SpeakerBuffer());
			buf.SenderName = senderName;
			buf.LastSeen = DateTime.UtcNow;

			while (buf.Chunks.Count >= 20)
				buf.Chunks.TryDequeue(out _);

			buf.Chunks.Enqueue(monoChunk);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(decodeBuf);
			ArrayPool<byte>.Shared.Return(opusTemp);
		}
	}

	/// <summary>
	/// Основной цикл микшера, выполняющий обработку аудио данных.
	/// Отвечает за синхронизацию, смешивание PCM-данных, кодирование звука
	/// и передачу результата следующему потребителю.
	/// </summary>
	/// <remarks>
	/// Этот метод работает в отдельном фоне-потоке. Использует таймер для корректной
	/// синхронизации микширования с требуемой частотой кадров. Выполняет периодическую
	/// очистку состояния для предотвращения накопления лишних данных.
	/// </remarks>
	private void MixerLoop()
	{
		var clock = Stopwatch.StartNew();
		long tickIndex = 0;

		var mixBuf = new short[FrameSamples];
		var pcmBytes = new byte[FrameSamples * 2];
		var encodeBuf = new byte[4096];

		while (!_cts.IsCancellationRequested)
		{
			long targetTicks = tickIndex * FrameMs * Stopwatch.Frequency / 1000;
			long remainTicks = targetTicks - clock.ElapsedTicks;
			while (remainTicks > 0)
			{
				long remainMs = remainTicks * 1000 / Stopwatch.Frequency;
				if (remainMs >= 2)
					Thread.Sleep(1);
				else
					Thread.SpinWait(1);
				remainTicks = targetTicks - clock.ElapsedTicks;
			}

			tickIndex++;

			try
			{
				Tick(mixBuf, pcmBytes, encodeBuf);
			}
			catch (Exception ex)
			{
				Log.Error(ex, "VoiceRelay: mixer tick error");
			}

			if (tickIndex % 250 == 0)
				Cleanup();
		}
	}

	/// <summary>
	/// Выполняет обработку голосового сигнала, включая сбор активных спикеров,
	/// смешивание PCM-данных, добавление радиошума, преобразование PCM в байты,
	/// кодирование Opus, а также распределение аудио среди получателей.
	/// </summary>
	/// <param name="mixBuf">Буфер для хранения смешанных PCM-данных.</param>
	/// <param name="pcmBytes">Буфер для преобразования PCM-данных в байтовый формат перед кодированием.</param>
	/// <param name="encodeBuf">Буфер для хранения закодированного аудио Opus.</param>
	/// <remarks>
	/// Этот метод использует активных спикеров, вычисляет смешанный аудиосигнал, добавляет
	/// розоватый шум для реалистичности, конвертирует данные в байтовый формат, кодирует их в Opus,
	/// после чего производит отправку аудиоданных соответствующим получателям, исключая активных
	/// спикеров из получателей.
	/// </remarks>
	private void Tick(short[] mixBuf, byte[] pcmBytes, byte[] encodeBuf)
	{
		// Собираем активных спикеров
		var activeSpeakers = new List<(ClientId id, short[] pcm, SpeakerBuffer buf)>();
		foreach (var (id, buf) in _speakers)
		{
			if (buf.Chunks.TryDequeue(out var chunk))
			{
				activeSpeakers.Add((id, chunk, buf));
			}
			else if (buf.LastSeen > DateTime.UtcNow - TimeSpan.FromSeconds(1))
			{
				// Спикер активен, но чанка нет — пропускаем (тишина)
			}
		}

		if (activeSpeakers.Count == 0) return;

		_tickIndex++;

		// Mix PCM
		Array.Clear(mixBuf, 0, FrameSamples);
		foreach (var (_, pcm, _) in activeSpeakers)
		{
			int len = Math.Min(pcm.Length, FrameSamples);
			for (int i = 0; i < len; i++)
			{
				int sum = mixBuf[i] + pcm[i];
				mixBuf[i] = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
			}
		}

		// Радио-шум: розоватый (частично low-pass), тише голоса.
		// Медленно меняем амплитуду каждые ~2 секунды для живости.
		var rng = _random.Value;
		const double alpha = 0.90; // меньше низких, больше средних частот
		if (_tickIndex % 100 == 0)
			if (rng != null)
				_currentNoiseAmp = 3500.0 * (0.7 + 0.6 * rng.NextDouble());

		for (int i = 0; i < FrameSamples; i++)
		{
			if (rng != null)
			{
				double white = rng.NextDouble() + rng.NextDouble() - 1.0;
				_noiseState = _noiseState * alpha + white * (1.0 - alpha);
			}

			mixBuf[i] = (short)Math.Clamp(mixBuf[i] + _noiseState * _currentNoiseAmp, short.MinValue, short.MaxValue);
		}

		// PCM → bytes
		for (int i = 0; i < FrameSamples; i++)
		{
			pcmBytes[i * 2] = (byte)(mixBuf[i] & 0xFF);
			pcmBytes[i * 2 + 1] = (byte)((mixBuf[i] >> 8) & 0xFF);
		}

		// Encode → Opus (mono, VoIP)
		if (_encoder == null)
			_encoder = OpusEncoder.Create(SampleRate, 1, Application.Voip);

		var encoded = _encoder.Encode(
			pcmBytes.AsSpan(0, FrameSamples * 2),
			encodeBuf.Length,
			encodeBuf.AsSpan());

		if (encoded.Length == 0) return;

		// Определяем получателей
		RefreshClientCache();
		var recipientSet = new HashSet<ClientId>();

		if (_testSettings.Enabled)
		{
			if (TryGetClientId(_testSettings.TargetNickname, out var targetId))
				recipientSet.Add(targetId);
		}
		else
		{
			foreach (var (_, _, buf) in activeSpeakers)
			{
				if (!_positionService.TryGetByName(buf.SenderName, out var senderPos) || senderPos == null)
					continue;

				var candidates = senderPos.IsInLobby || senderPos.Category == CategoryObject.Spectator
					? _positionService.GetLobbyRecipients(senderPos.Coalition)
					: _positionService.GetInSphere(
						senderPos.Coalition, senderPos.X, senderPos.Y, senderPos.Z,
						_settings.MaxDistance);

				foreach (var player in candidates)
				{
					if (player.GamerName == buf.SenderName) continue;
					if (_settings.CoalitionCheck && player.Coalition != senderPos.Coalition) continue;
					if (!TryGetClientId(player.GamerName, out var recipientId)) continue;
					recipientSet.Add(recipientId);
				}
			}
		}

		// Half-duplex: активные спикеры не слышат mixed audio
		foreach (var (speakerId, _, _) in activeSpeakers)
			recipientSet.Remove(speakerId);

		if (recipientSet.Count == 0) return;

		var recipients = recipientSet.ToList();
		var packetId = _tsClient.AllocateVoiceWhisperId();
		_tsClient.SendAudioWhisper(
			encoded, Codec.OpusVoice,
			Array.Empty<ChannelId>(),
			recipients,
			packetId);
	}

	/// <summary>
	/// Удаляет устаревшие буферы динамиков и декодеры для клиентов, которые
	/// неактивны в течение определенного времени.
	/// </summary>
	/// <remarks>
	/// Метод выполняет проверку каждого клиента в словаре динамиков (_speakers).
	/// Если клиент не подавал сигналов в течение 10 секунд и его очередь фрагментов пуста,
	/// он считается устаревшим и удаляется из _speakers. Также выполняется удаление
	/// связанных с клиентом декодеров из словаря _decoders, для предотвращения утечек памяти.
	/// </remarks>
	private void Cleanup()
	{
		var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(10);
		foreach (var (id, buf) in _speakers.ToArray())
		{
			if (buf.LastSeen < cutoff && buf.Chunks.IsEmpty)
			{
				_speakers.TryRemove(id, out _);
				foreach (var key in _decoders.Keys.ToArray())
					if (key.Item1 == id)
					{
						if (_decoders.TryRemove(key, out var dec))
							dec.Dispose();
					}
			}
		}
	}

	/// <summary>
	/// Обновляет кеш клиентов с сервера TeamSpeak.
	/// Очищает существующий кеш и заново заполняет его информацией о клиентах,
	/// актуальной на момент вызова метода.
	/// </summary>
	/// <remarks>
	/// Для предотвращения слишком частых обновлений метод выполняет проверку интервала времени
	/// с момента последнего обновления кеша. Если прошло менее пяти секунд, обновление не выполняется.
	/// Кешируются только клиенты с ненулевыми именами. Данные обновляются из свойств подключения,
	/// предоставленных объектом TeamSpeak клиента.
	/// </remarks>
	private void RefreshClientCache()
	{
		var now = DateTime.UtcNow;
		if (now - _lastClientCacheRefresh < TimeSpan.FromSeconds(5)) return;
		_clientIdCache.Clear();
		foreach (var kvp in _tsClient.Book.Clients)
			if (!string.IsNullOrEmpty(kvp.Value.Name))
				_clientIdCache[kvp.Value.Name] = kvp.Key;
		_lastClientCacheRefresh = now;
	}

	/// <summary>
	/// Пытается получить идентификатор клиента по его имени.
	/// Проверяет кэш идентификаторов клиентов, а затем, при необходимости, выполняет
	/// полный перебор всех клиентов, чтобы найти соответствие.
	/// </summary>
	/// <param name="name">Имя клиента, для которого требуется получить идентификатор.</param>
	/// <param name="clientId">Выходной параметр, содержащий найденный идентификатор клиента,
	/// если операция успешна.</param>
	/// <returns>true, если удалось найти идентификатор клиента;
	/// false, если клиент не найден.</returns>
	private bool TryGetClientId(string name, out ClientId clientId)
	{
		if (_clientIdCache.TryGetValue(name, out clientId)) return true;
		foreach (var kvp in _tsClient.Book.Clients)
		{
			if (string.Equals(kvp.Value.Name, name, StringComparison.OrdinalIgnoreCase))
			{
				_clientIdCache[name] = kvp.Key;
				clientId = kvp.Key;
				return true;
			}
		}

		return false;
	}

	public void Dispose()
	{
		_cts.Cancel();
		_mixerThread.Join(TimeSpan.FromSeconds(2));
		_encoder?.Dispose();
		foreach (var d in _decoders.Values) d.Dispose();
		_decoders.Clear();
	}
}
