using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
	/// Логер, используемый для записи данных о процессах и ошибках,
	/// связанных с работой класса VoiceRelayPipe.
	/// </summary>
	private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<VoiceRelayPipe>();

	/// <summary>
	/// Клиент TeamSpeak, используемый для обработки аудио и управления взаимодействиями
	/// с сервером, включая передачу и прием аудио данных.
	/// </summary>
	private readonly TsFullClient _tsClient;

	/// <summary>
	/// Сервис управления позициями игроков.
	/// Используется для получения информации о нахождении игроков в определенных зонах, их состоянии
	/// (лобби или активная игра) и выполнения операций, связанных с обработкой данных о позициях.
	/// </summary>
	private readonly PlayerPositionService _positionService;

	/// <summary>
	/// Настройки ретрансляции аудиопотока.
	/// Используются для задания максимальной дистанции передачи,
	/// проверки коалиционной принадлежности, отключения радиальных эффектов,
	/// а также настройки качества радио при передаче данных.
	/// </summary>
	private readonly RelaySettings _settings;

	/// <summary>
	/// Настройки тестового воспроизведения голосового потока.
	/// Используется для активации или деактивации тестового режима
	/// и определения целевого пользователя для воспроизведения.
	/// </summary>
	private readonly TestVoicePlaybackSettings _testSettings;

	/// <summary>
	/// Приватное поле, содержащее ссылку на следующий объект аудиопотока, реализующий интерфейс <see cref="IAudioPassiveConsumer"/>.
	/// Используется для передачи обработанных или исходных аудиоданных вниз по аудиопайплайну.
	/// Может быть установлено внешним объектом через свойство <see cref="OutStream"/>.
	/// </summary>
	private IAudioPassiveConsumer? _nextPipe;

	/// <summary>
	/// Частота дискретизации аудиосигнала, используемая в обработке данных.
	/// Задается в герцах (Hz). Обеспечивает совместимость с настройками кодека
	/// и генерацию аудиопотока с заданной четкостью.
	/// </summary>
	private const int SampleRate = 48000;

	/// <summary>
	/// Время в миллисекундах для одной звуковой рамки, используемой
	/// в процессе смешивания и передачи аудио данных в VoiceRelayPipe.
	/// Определяет длительность базовой единицы данных при обработке звука.
	/// </summary>
	private const int FrameMs = 20;

	/// <summary>
	/// Количество аудиосэмплов в одном кадре при заданной частоте дискретизации и длительности кадра.
	/// Рассчитывается как произведение частоты дискретизации на длительность кадра в миллисекундах,
	/// делённое на 1000 (для перевода миллисекунд в секунды).
	/// </summary>
	private const int FrameSamples = SampleRate / 1000 * FrameMs; // 960

	/// <summary>
	/// Текущее значение drive для soft-clip эффекта.
	/// Интерполируется плавно к <see cref="_driveTarget"/>.
	/// </summary>
	private double _currentDrive = 1.0;

	/// <summary>
	/// Целевое значение drive для soft-clip эффекта.
	/// Устанавливается случайным образом при burst'ах перегруза.
	/// </summary>
	private double _driveTarget = 1.0;

	/// <summary>
	/// Счётчик тиков, оставшихся до окончания текущего burst'а перегруза.
	/// </summary>
	private int _driveHoldTicks;

	/// <summary>
	/// Базовая амплитуда фонового шума рации.
	/// </summary>
	private const float BaseNoiseAmp = 600f;

	/// <summary>
	/// Биквадратный (biquad) фильтр второго порядка.
	/// Используется для полосовой фильтрации голоса и шума.
	/// </summary>
	private class BiquadFilter
	{
		/// <summary>
		/// Коэффициент передачи для текущего входного сэмпла в рамках биквадратного фильтра.
		/// Используется для вычисления отфильтрованного значения на основе входных и выходных данных.
		/// </summary>
		private double _b0, _b1, _b2, _a1, _a2;

		/// <summary>
		/// Последнее входное значение, используемое в расчётах фильтра.
		/// Хранит промежуточное состояние для обеспечения непрерывности обработки сигнала.
		/// </summary>
		private double _x1, _x2, _y1, _y2;

		/// <summary>
		/// Создаёт high-pass фильтр Баттерворта 2-го порядка.
		/// </summary>
		public static BiquadFilter HighPass(double freq, double sampleRate)
		{
			double w0 = 2 * Math.PI * freq / sampleRate;
			double cos = Math.Cos(w0), sin = Math.Sin(w0);
			double q = 0.7071;
			double alpha = sin / (2 * q);
			double b0 = (1 + cos) / 2, b1 = -(1 + cos), b2 = (1 + cos) / 2;
			double a0 = 1 + alpha, a1 = -2 * cos, a2 = 1 - alpha;
			return new BiquadFilter { _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b2 / a0, _a1 = a1 / a0, _a2 = a2 / a0 };
		}

		/// <summary>
		/// Создаёт low-pass фильтр Баттерворта 2-го порядка.
		/// </summary>
		public static BiquadFilter LowPass(double freq, double sampleRate)
		{
			double w0 = 2 * Math.PI * freq / sampleRate;
			double cos = Math.Cos(w0), sin = Math.Sin(w0);
			double q = 0.7071;
			double alpha = sin / (2 * q);
			double b0 = (1 - cos) / 2, b1 = 1 - cos, b2 = (1 - cos) / 2;
			double a0 = 1 + alpha, a1 = -2 * cos, a2 = 1 - alpha;
			return new BiquadFilter { _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b2 / a0, _a1 = a1 / a0, _a2 = a2 / a0 };
		}

		/// <summary>
		/// Обрабатывает один сэмпл и возвращает отфильтрованное значение.
		/// </summary>
		public double Process(double x)
		{
			double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
			_x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
			return y;
		}
	}

	/// <summary>
	/// Буфер для хранения аудиоданных, связанных с определённым отправителем.
	/// Используется для организации очереди кусочков PCM-звука, поступающих от клиента.
	/// Содержит имя отправителя, последнюю отметку времени получения и очередь аудиоданных.
	/// </summary>
	private class SpeakerBuffer
	{
		/// <summary>
		/// Очередь аудиофреймов, представляющих собой звуковые данные, полученные от пользователя.
		/// Используется для обработки и микширования звука в реальном времени.
		/// </summary>
		public readonly ConcurrentQueue<short[]> Chunks = new();

		/// <summary>
		/// Уникальный идентификатор отправителя, используемый для связывания
		/// входящих аудиоданных с конкретным клиентом. Применяется в системах обработки,
		/// направленных на выявление источника звука и управление маршрутизацией аудио.
		/// </summary>
		public string SenderUid = "";

		/// <summary>
		/// Последнее время, когда активность спикера была зафиксирована.
		/// Используется для определения актуальности данных о спикере
		/// и очистки устаревших буферов.
		/// </summary>
		public DateTime LastSeen = DateTime.UtcNow;
	}

	/// <summary>
	/// Коллекция, представляющая собой словарь текущих активных участников
	/// аудиопотока. Ключом является идентификатор клиента, а значением —
	/// буфер, хранящий данные, связанные с участником.
	/// </summary>
	private readonly ConcurrentDictionary<ClientId, SpeakerBuffer> _speakers = new();

	/// <summary>
	/// Коллекция декодеров Opus, используемая для декодирования аудиопотоков.
	/// Организована как конкурентный словарь, где ключом является комбинация идентификатора клиента
	/// и используемого кодека, а значением — соответствующий декодер.
	/// </summary>
	private readonly ConcurrentDictionary<(ClientId, Codec), OpusDecoder> _decoders = new();

	/// <summary>
	/// Токен источника отмены, используемый для управления асинхронными операциями,
	/// связанными с микшерным потоком в классе VoiceRelayPipe.
	/// </summary>
	private readonly CancellationTokenSource _cts = new();

	/// <summary>
	/// Фоновый поток, отвечающий за выполнение цикла микширования аудио.
	/// Поток обрабатывает данные, передаваемые от декодеров, объединяет их и передает микшированный поток далее.
	/// </summary>
	private readonly Thread _mixerThread;

	/// <summary>
	/// Экземпляр кодировщика Opus, используемый для преобразования аудиоданных
	/// PCM в формат Opus с целью уменьшения размера данных и обеспечения совместимости
	/// с протоколами передачи звука.
	/// </summary>
	private OpusEncoder? _encoder;

	/// <summary>
	/// Генератор случайных чисел, используемый в потоке микшера.
	/// Применяется для редких событий (треск, флуктуации амплитуды).
	/// </summary>
	private readonly Random _rng = new();

	/// <summary>
	/// Фильтр высоких частот для голоса (300 Гц).
	/// Убирает низкий rumble, характерный для обычного микрофона.
	/// </summary>
	private readonly BiquadFilter _voiceHp = BiquadFilter.HighPass(300, SampleRate);

	/// <summary>
	/// Фильтр низких частот для голоса (3400 Гц).
	/// Обрезает верхние частоты, имитируя полосу пропускания рации.
	/// </summary>
	private readonly BiquadFilter _voiceLp = BiquadFilter.LowPass(3400, SampleRate);

	/// <summary>
	/// Фильтр высоких частот для шума (800 Гц).
	/// Формирует нижнюю границу спектра радиошума.
	/// </summary>
	private readonly BiquadFilter _noiseHp = BiquadFilter.HighPass(800, SampleRate);

	/// <summary>
	/// Фильтр низких частот для шума (4000 Гц).
	/// Формирует верхнюю границу спектра радиошума.
	/// </summary>
	private readonly BiquadFilter _noiseLp = BiquadFilter.LowPass(4000, SampleRate);

	/// <summary>
	/// Состояние LCG-генератора белого шума.
	/// Используется для быстрого генератора псевдослучайных чисел в горячем цикле.
	/// </summary>
	private uint _lcgState = 123456789u;

	/// <summary>
	/// Текущая амплитуда треска (краткого импульсного шума).
	/// Затухает каждый тик; резко нарастает при случайном событии.
	/// </summary>
	private float _crackleAmp;

	/// <summary>
	/// Амплитуда короткого squelch-хвоста («пшика») при отпускании тангенты.
	/// Задаётся случайным всплеском один раз за паузу и быстро затухает.
	/// </summary>
	private float _squelchTail;

	/// <summary>
	/// Флаг, что пшик уже был произведён в текущей паузе между передачами.
	/// Сбрасывается при появлении новых спикеров.
	/// </summary>
	private bool _squelchPopped;

	/// <summary>
	/// Список активных спикеров, содержащий их идентификаторы, PCM-данные и буферы.
	/// Используется для обработки и микширования аудио данных от участников.
	/// </summary>
	private readonly List<(ClientId id, short[] pcm, SpeakerBuffer buf)> _activeSpeakers = new(16);

	/// <summary>
	/// Набор идентификаторов клиентов, которым предназначены закодированные аудио-пакеты.
	/// Используется для управления списком получателей и фильтрации активных спикеров
	/// в процессе работы микшера звука.
	/// </summary>
	private readonly HashSet<ClientId> _recipientSet = new(64);

	/// <summary>
	/// Список идентификаторов клиентов, которым предназначен смешанный аудиопоток.
	/// Этот список формируется динамически в зависимости от текущих активных спикеров
	/// и активных настроек, таких как тестовые воспроизведения и режим половинного дуплекса.
	/// </summary>
	private readonly List<ClientId> _recipients = new(64);

	/// <summary>
	/// Кэш последних получателей, используемый для squelch-хвоста.
	/// Когда активные спикеры отсутствуют, но squelch-шум ещё слышен,
	/// аудио отправляется этим клиентам.
	/// </summary>
	private readonly HashSet<ClientId> _lastRecipientSet = new(64);

	/// <summary>
	/// Список идентификаторов клиентов, подлежащих очистке. Используется в процессе очистки буферов динамиков,
	/// которые больше не активны или чьи данные устарели.
	/// </summary>
	private readonly List<ClientId> _cleanupSpeakers = new(16);

	/// <summary>
	/// Список кодеков и идентификаторов клиентов, используемый для очистки декодеров,
	/// связанных с обработкой аудиопотоков в рамках работы класса VoiceRelayPipe.
	/// </summary>
	private readonly List<(ClientId, Codec)> _cleanupDecoders = new(16);

	/// <summary>
	/// Кэш идентификаторов клиентов. Предоставляет сопоставление между именами клиентов и их идентификаторами.
	/// Доступ возможен только из потока микшера, безопасен для многопоточного доступа благодаря особенностям реализации.
	/// Используется для повышения производительности при работе с клиентами TeamSpeak.
	/// </summary>
	private readonly Dictionary<string, ClientId> _clientIdCache = new();

	/// <summary>
	/// Временная метка последнего обновления кеша клиентов.
	/// Используется для оптимизации частоты обновлений данных о клиентах.
	/// </summary>
	private DateTime _lastClientCacheRefresh = DateTime.MinValue;

	/// <summary>
	/// Свойство, предоставляющее доступ к следующему объекту в аудиопайплайне,
	/// реализующему интерфейс <see cref="IAudioPassiveConsumer"/>.
	/// Указывает поток, в который передаются обработанные аудиоданные.
	/// Может быть установлено для изменения цепочки обработки аудиоданных.
	/// </summary>
	public IAudioPassiveConsumer? OutStream { get => _nextPipe; set => _nextPipe = value; }

	/// <summary>
	/// Указывает, активен ли текущий потребитель звуковых данных.
	/// Если значение возвращается как true, объект готов принимать аудиоданные.
	/// </summary>
	public bool Active => true;

	/// <summary>
	/// Класс предоставляет функциональность обработки звука с использованием PCM-микшера.
	/// Выполняет декодирование, смешивание и последующее кодирование аудиопотока,
	/// после чего отправляет полученный обработанный поток всем получателям через whisper.
	/// Класс не поддерживает радиоэффекты и отдельное кодирование на каждого получателя.
	/// Для каждого потока от бота создается один объединенный (mixed) поток.
	/// </summary>
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
	/// Мягкий клиппинг (soft-clip) на основе гиперболического тангенса.
	/// Имитирует перегруз усилителя рации, придавая звуку характерную "плотность".
	/// Интенсивность перегруза задаётся параметром <paramref name="drive"/>.
	/// </summary>
	private static short SoftClip(short sample, double drive)
	{
		double x = sample * drive / 32768.0;
		double clipped = Math.Tanh(x);
		return (short)(clipped * 32767.0);
	}

	/// <summary>
	/// Генерирует следующее значение белого шума через LCG.
	/// Используется в горячем цикле вместо <see cref="Random.NextDouble"/> для производительности.
	/// </summary>
	private float NextLcgNoise()
	{
		_lcgState = _lcgState * 1664525u + 1013904223u;
		return (_lcgState / (float)uint.MaxValue) * 2.0f - 1.0f;
	}

	/// <summary>
	/// Метод выполняет обработку и передачу данных аудиопотока следующему потребителю.
	/// Декодирует аудиоданные, проверяет их соответствие определенным условиям,
	/// перед отправкой производит дополнительные проверки, такие как коалиция отправителя,
	/// режим whisper и тестовый режим. Если все условия выполнены, обрабатывает
	/// декодированные данные.
	/// </summary>
	/// <param name="data">Секция данных аудиопотока для обработки.</param>
	/// <param name="meta">Метаданные, связанные с передаваемым аудиопотоком.</param>
	public void Write(Span<byte> data, Meta? meta)
	{
		_nextPipe?.Write(data, meta);

		if (data.Length < 6) return;

		var realClientId = (ClientId)BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
		var codec = (Codec)data[4];

		if (!_tsClient.Book.Clients.TryGetValue(realClientId, out var client)) return;
		var senderName = client.Name;
		if (string.IsNullOrEmpty(senderName)) return;
		var senderUid = client.Uid?.Value;
		if (string.IsNullOrWhiteSpace(senderUid)) return;
		if (!(meta?.In.Whisper ?? false)) return;

		bool isTestMode = _testSettings.Enabled;

		if (!isTestMode)
		{
			if (!_positionService.TryGetByTs3Uid(senderUid, out var sender) || sender == null) return;
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

			short[] monoChunk = ArrayPool<short>.Shared.Rent(FrameSamples);
			monoChunk.AsSpan(0, FrameSamples).Clear();

			try
			{
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
				buf.SenderUid = senderUid;
				buf.LastSeen = DateTime.UtcNow;

				while (buf.Chunks.Count >= 20)
				{
					if (buf.Chunks.TryDequeue(out var oldChunk))
						ArrayPool<short>.Shared.Return(oldChunk);
				}

				buf.Chunks.Enqueue(monoChunk);
				monoChunk = null!; // владение передано очереди
			}
			finally
			{
				if (monoChunk != null)
					ArrayPool<short>.Shared.Return(monoChunk);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(decodeBuf);
			ArrayPool<byte>.Shared.Return(opusTemp);
		}
	}

	/// <summary>
	/// Основной метод обработки цикла микширования звука. Этот метод выполняет следующие задачи:
	/// - Управляет таймингом для отправки аудиокадров, используя высокоточное измерение времени.
	/// - Выполняет микширование аудиоданных с использованием заданного буфера.
	/// - Кодирует и передает обработанный аудиопоток следующим компонентам через цепочку OutStream.
	/// - Запускает регулярную очистку внутренних ресурсов каждые 250 итераций.
	/// Данный метод работает в отдельном потоке с высоким приоритетом, чтобы обеспечить минимальные задержки
	/// в обработке аудиопотока.
	/// Если во время выполнения цикла происходит ошибка, она логируется, чтобы избежать прерывания работы цикла.
	/// Метод продолжает свою работу до получения сигнала отмены через CancellationToken.
	/// </summary>
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
				else if (remainMs >= 1)
					Thread.Sleep(0);
				else
					Thread.SpinWait(20);
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
	/// Выполняет обработку одного цикла микшера, включая сбор данных активных спикеров,
	/// смешивание PCM данных, добавление радиоэффектов, кодирование аудиопотока в формат Opus,
	/// определение получателей и отправку обработанного звука через whisper.
	/// </summary>
	/// <param name="mixBuf">Буфер для смешивания PCM данных активных спикеров.</param>
	/// <param name="pcmBytes">Буфер для преобразования PCM данных в байтовое представление.</param>
	/// <param name="encodeBuf">Буфер для хранения закодированного аудиопотока.</param>
	private void Tick(short[] mixBuf, byte[] pcmBytes, byte[] encodeBuf)
	{
		// Собираем активных спикеров
		_activeSpeakers.Clear();
		foreach (var (id, buf) in _speakers)
		{
			if (buf.Chunks.TryDequeue(out var chunk))
			{
				_activeSpeakers.Add((id, chunk, buf));
			}
		}

		bool hasSpeakers = _activeSpeakers.Count > 0;

		// Squelch-логика: короткий «пшик» при отпускании тангенты
		if (hasSpeakers)
		{
			_squelchTail = 0f;
			_squelchPopped = false; // готовимся к следующей паузе
		}
		else
		{
			// Один пшик за паузу, потом только затухание
			if (!_squelchPopped && _rng.NextDouble() < 0.75)
			{
				_squelchTail = 1000f + (float)(_rng.NextDouble() * 1200f);
				_squelchPopped = true;
			}
			else
			{
				_squelchTail *= 0.35f; // быстрое затухание
			}

			if (_squelchTail < 15f)
				_squelchTail = 0f;
		}

		// Если нет спикеров и хвост затух — пропускаем кадр
		if (!hasSpeakers && _squelchTail <= 0f)
			return;

		try
		{
			// Mix PCM
			Array.Clear(mixBuf, 0, FrameSamples);
			if (hasSpeakers)
			{
				foreach (var (_, pcm, _) in _activeSpeakers)
				{
					for (int i = 0; i < FrameSamples; i++)
					{
						int sum = mixBuf[i] + pcm[i];
						mixBuf[i] = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
					}
				}
			}

			// Полосовая фильтрация голоса: 300–3400 Гц
			if (hasSpeakers)
			{
				for (int i = 0; i < FrameSamples; i++)
				{
					double s = _voiceHp.Process(mixBuf[i]);
					s = _voiceLp.Process(s);
					mixBuf[i] = (short)Math.Clamp((int)s, short.MinValue, short.MaxValue);
				}

				// Soft clip — перегруз усилителя с динамической огибающей
				if (hasSpeakers && _driveHoldTicks <= 0 && _rng.NextDouble() < 0.03)
				{
					_driveTarget = 1.3 + _rng.NextDouble() * 2.7; // 1.3 … 4.0
					_driveHoldTicks = 2 + _rng.Next(5);           // 2–6 тиков (40–120 мс)
				}

				if (_driveHoldTicks > 0)
				{
					_driveHoldTicks--;
					if (_driveHoldTicks == 0)
						_driveTarget = 1.0;
				}

				_currentDrive += (_driveTarget - _currentDrive) * 0.4;

				for (int i = 0; i < FrameSamples; i++)
					mixBuf[i] = SoftClip(mixBuf[i], _currentDrive);
			}

			// Генерация шума и крэклов
			if (_rng.NextDouble() < 0.0005)
				_crackleAmp = 6000f + (float)(_rng.NextDouble() * 4000);
			else
				_crackleAmp *= 0.97f;

			float noiseAmp = BaseNoiseAmp + _squelchTail;

			for (int i = 0; i < FrameSamples; i++)
			{
				float white = NextLcgNoise();
				double filtered = _noiseHp.Process(white);
				filtered = _noiseLp.Process(filtered);

				float noise = (float)filtered * noiseAmp;
				float crackle = _crackleAmp > 1f
					? NextLcgNoise() * _crackleAmp
					: 0f;

				int mixed = mixBuf[i] + (int)noise + (int)crackle;
				mixBuf[i] = (short)Math.Clamp(mixed, short.MinValue, short.MaxValue);
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
			_recipientSet.Clear();

			if (hasSpeakers)
			{
				if (_testSettings.Enabled)
				{
					if (!string.IsNullOrWhiteSpace(_testSettings.TargetTs3Uid) && TryGetClientId(_testSettings.TargetTs3Uid, out var targetId))
						_recipientSet.Add(targetId);
				}
				else
				{
					foreach (var (_, _, buf) in _activeSpeakers)
					{
						if (!_positionService.TryGetByTs3Uid(buf.SenderUid, out var senderPos) || senderPos == null)
							continue;

						var candidates = senderPos.IsInLobby || senderPos.Category == CategoryObject.Spectator
							? _positionService.GetLobbyRecipients(senderPos.Coalition)
							: _positionService.GetInSphere(
								senderPos.Coalition, senderPos.X, senderPos.Y, senderPos.Z,
								_settings.MaxDistance);

						foreach (var player in candidates)
						{
							if (player.Ts3Uid == senderPos.Ts3Uid) continue;
							if (_settings.CoalitionCheck && player.Coalition != senderPos.Coalition) continue;
							if (!TryGetClientId(player.Ts3Uid!, out var recipientId)) continue;
							_recipientSet.Add(recipientId);
						}
					}
				}

				// Half-duplex: активные спикеры не слышат mixed audio
				foreach (var (speakerId, _, _) in _activeSpeakers)
					_recipientSet.Remove(speakerId);

				if (_recipientSet.Count > 0)
				{
					_lastRecipientSet.Clear();
					_lastRecipientSet.UnionWith(_recipientSet);
				}
				else
				{
					_lastRecipientSet.Clear();
				}
			}
			else
			{
				// Нет спикеров — squelch-хвост отправляем последним получателям
				_recipientSet.Clear();
				_recipientSet.UnionWith(_lastRecipientSet);
			}

			if (_recipientSet.Count == 0) return;

			_recipients.Clear();
			_recipients.AddRange(_recipientSet);

			var packetId = _tsClient.AllocateVoiceWhisperId();
			_tsClient.SendAudioWhisper(
				encoded, Codec.OpusVoice,
				Array.Empty<ChannelId>(),
				_recipients,
				packetId);
		}
		finally
		{
			// Гарантированно возвращаем арендованные чанки в пул
			foreach (var (_, pcm, _) in _activeSpeakers)
				ArrayPool<short>.Shared.Return(pcm);
		}
	}

	/// <summary>
	/// Метод реализует очистку устаревших и неактивных потоков данных, используемых в PCM-микшере.
	/// Удаляет буферы и декодеры, которые не использовались в течение заданного времени, освобождая ресурсы.
	/// </summary>
	private void Cleanup()
	{
		var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(10);

		_cleanupSpeakers.Clear();
		foreach (var (id, buf) in _speakers)
		{
			if (buf.LastSeen < cutoff && buf.Chunks.IsEmpty)
				_cleanupSpeakers.Add(id);
		}

		foreach (var id in _cleanupSpeakers)
		{
			if (_speakers.TryRemove(id, out var buf))
			{
				while (buf.Chunks.TryDequeue(out var chunk))
					ArrayPool<short>.Shared.Return(chunk);
			}

			_cleanupDecoders.Clear();
			foreach (var key in _decoders.Keys)
				if (key.Item1 == id)
					_cleanupDecoders.Add(key);

			foreach (var key in _cleanupDecoders)
				if (_decoders.TryRemove(key, out var dec))
					dec.Dispose();
		}
	}

	/// <summary>
	/// Обновляет кеш клиентов на основе данных из сервера TeamSpeak.
	/// Кеш содержит соответствия между именами клиентов и их уникальными идентификаторами.
	/// Повторное обновление кеша производится только если с момента последнего обновления прошло не менее 5 секунд.
	/// Метод предназначен для вызова только из потока микшера, так как внешние вызовы могут привести к непредсказуемым результатам.
	/// </summary>
	private void RefreshClientCache()
	{
		var now = DateTime.UtcNow;
		if (now - _lastClientCacheRefresh < TimeSpan.FromSeconds(5)) return;
		_clientIdCache.Clear();
		foreach (var kvp in _tsClient.Book.Clients)
		{
			var uid = kvp.Value.Uid?.Value;
			if (!string.IsNullOrWhiteSpace(uid))
				_clientIdCache[uid] = kvp.Key;
		}
		_lastClientCacheRefresh = now;
	}

	/// <summary>
	/// Пытается получить идентификатор клиента по его TS3 UID.
	/// </summary>
	/// <param name="uid">TS3 UID клиента, для которого необходимо получить идентификатор.</param>
	/// <param name="clientId">Выходной параметр, содержащий идентификатор клиента, если он был найден.</param>
	/// <returns>Возвращает true, если идентификатор клиента успешно найден; в противном случае false.</returns>
	private bool TryGetClientId(string uid, out ClientId clientId)
	{
		if (_clientIdCache.TryGetValue(uid, out clientId)) return true;
		foreach (var kvp in _tsClient.Book.Clients)
		{
			if (kvp.Value.Uid?.Value == uid)
			{
				_clientIdCache[uid] = kvp.Key;
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

		// Дрейним все очереди и возвращаем массивы в пул
		foreach (var (_, buf) in _speakers)
		{
			while (buf.Chunks.TryDequeue(out var chunk))
				ArrayPool<short>.Shared.Return(chunk);
		}
		_speakers.Clear();

		foreach (var d in _decoders.Values) d.Dispose();
		_decoders.Clear();
	}
}
