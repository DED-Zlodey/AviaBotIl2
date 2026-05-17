using AviaBot.Models;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TSLib.Audio;
using TSLib.Audio.Opus;
using TSLib;
using TSLib.Full;

namespace AviaBot.Services;

public class VoiceRecorderPipe : IAudioPassiveConsumer, IAudioActiveProducer, IDisposable
{
	/// <summary>
	/// Логгер, используемый для записи информации, предупреждений и ошибок,
	/// связанных с различной работой, выполняемой в классе VoiceRecorderPipe.
	/// </summary>
	private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<VoiceRecorderPipe>();

	/// <summary>
	/// Путь к директории, в которой сохраняются записи,
	/// полученные из голосового потока. Если путь не указан,
	/// используется директория по умолчанию "recordings" в корневой папке приложения.
	/// </summary>
	private readonly string _recordingsPath;

	/// <summary>
	/// Клиент TsFull, используемый для взаимодействия с сервером TeamSpeak и обработки
	/// различных событий, таких как передача аудио, управление каналами и пользователями.
	/// </summary>
	private readonly TsFullClient _tsClient;

	/// <summary>
	/// Флаг, указывающий, включена ли функциональность записи голоса
	/// в компоненте VoiceRecorderPipe.
	/// Если значение равно true, то данный компонент записывает
	/// входящий аудиопоток; если false — запись отключена.
	/// </summary>
	private readonly bool _enabled;

	/// <summary>
	/// Тайм-аут в миллисекундах, используемый для завершения обработки аудиопотока,
	/// если за указанный интервал времени не было получено новых данных.
	/// </summary>
	private readonly int _speechTimeoutMs;

	/// <summary>
	/// Приватное поле, представляющее следующий звуковой обработчик (пассивного потребителя),
	/// которому будут передаваться звуковые данные после обработки текущим объектом VoiceRecorderPipe.
	/// </summary>
    private IAudioPassiveConsumer? _nextPipe;

    /// <summary>
    /// Хранит коллекцию аудиопакетов, организованных по идентификаторам клиентов.
    /// Используется для временного сохранения данных клиентов во время их обработки.
    /// Основным ключом является идентификатор клиента <see cref="ClientId"/>,
    /// значением — список аудиопакетов, ассоциированных с этим клиентом.
    /// </summary>
    private readonly ConcurrentDictionary<ClientId, List<AudioPacket>> _clientPackets = new();

    /// <summary>
    /// Коллекция таймеров, связанных с клиентами. Используется для управления временем
    /// обработки или ожидания активности клиентов в пределах системы. В качестве ключа
    /// выступает идентификатор клиента, а значением является экземпляр таймера, который
    /// регулирует операции, связанные с данным клиентом.
    /// </summary>
    private readonly ConcurrentDictionary<ClientId, Timer> _clientTimers = new();

    /// <summary>
    /// Объект синхронизации, используемый для управления доступом к разделяемым ресурсам
    /// в многопоточной среде внутри класса VoiceRecorderPipe.
    /// </summary>
    private readonly object _lock = new object();

    /// <summary>
    /// Словарь, содержащий соответствие пары идентификатора клиента и кодека
    /// к экземплярам OpusDecoder. Используется для декодирования аудиоданных
    /// в зависимости от источника и типа кодека.
    /// </summary>
    private readonly Dictionary<(ClientId, Codec), OpusDecoder> _decoders = new Dictionary<(ClientId, Codec), OpusDecoder>();

    /// <summary>
    /// Буфер, используемый для хранения декодированных аудиоданных перед их дальнейшей обработкой
    /// или передачей следующим компонентам в обработке аудиопотока.
    /// </summary>
    private readonly byte[] _decodedBuffer = new byte[4096 * 2];

    /// <summary>
    /// Свойство, представляющее собой следующий компонент в аудиопотоке,
    /// который принимает данные для дальнейшей обработки или передачи.
    /// </summary>
    public IAudioPassiveConsumer? OutStream { get => _nextPipe; set => _nextPipe = value; }

    /// <summary>
    /// Свойство, указывающее, активен ли текущий компонент в обработке или производстве аудиопотока.
    /// </summary>
    public bool Active => true;

    /// <summary>
    /// Класс VoiceRecorderPipe реализует функциональность записи голосовых данных в файл.
    /// Используется в связке с TsFullClient для обработки и записи аудиопотоков.
    /// </summary>
    /// <remarks>
    /// Этот класс предоставляет возможность записи голосовых данных, поступающих от клиента TeamSpeak,
    /// с поддержкой временного ограничения на запись речи и опциональным указанием пути для сохранения записей.
    /// </remarks>
    public VoiceRecorderPipe(TsFullClient tsClient, bool enabled, string? outputPath = null, int speechTimeoutMs = 1500)
    {
        _tsClient = tsClient;
        _enabled = enabled;
        _speechTimeoutMs = speechTimeoutMs;
        _recordingsPath = Path.GetFullPath(outputPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory!, "recordings"));
        if (_enabled)
        {
            Directory.CreateDirectory(_recordingsPath);
        }
        Log.Information("VoiceRecorderPipe created - Enabled={0}, Path={1}, Timeout={2}ms", enabled, _recordingsPath, speechTimeoutMs);
    }

    /// <summary>
    /// Метод Write обрабатывает входящий аудиопоток в формате Span<byte> и метаданные Meta.
    /// Используется для обработки и анализа голосовых данных, включая работу с whisper-сообщениями.
    /// </summary>
    /// <param name="data">Данные аудиопотока в виде Span<byte>.</param>
    /// <param name="meta">Метаданные, содержащие дополнительную информацию о голосовом потоке.</param>
    public void Write(Span<byte> data, Meta? meta)
    {
        if (!_enabled)
        {
            _nextPipe?.Write(data, meta);
            return;
        }

        bool isWhisper = meta?.In.Whisper ?? false;
        var sender = meta?.In.Sender ?? default(ClientId);

        if (data.Length > 0 && isWhisper)
        {
            string clientName = "Unknown";
            if (_tsClient.Book.Clients.TryGetValue(sender, out var client))
                clientName = client.Name;

            try
            {
                // Формат входящего voice пакета: [VId:2][CId:2][Codec:1][Audio:N]
                if (data.Length < 6)
                {
                    Log.Information($"[WHISPER] {clientName}({sender}): packet too small ({data.Length} bytes)");
                }
                else
                {
                    var realClientId = (ClientId)BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
                    var codec = (Codec)data[4];
                    var audioData = data.Slice(5);

                    // Имя клиента по настоящему ClientId
                    if (_tsClient.Book.Clients.TryGetValue(realClientId, out var realClient))
                        clientName = realClient.Name;

                    if (codec == Codec.OpusVoice || codec == Codec.OpusMusic)
                    {
                        var decoder = GetDecoder(realClientId, codec);
                        int decodeOutputSize = codec == Codec.OpusVoice ? _decodedBuffer.Length / 2 : _decodedBuffer.Length;
                        var decoded = decoder.Decode(audioData, _decodedBuffer.AsSpan(0, decodeOutputSize));
                        int pcmLength = decoded.Length;

                        if (codec == Codec.OpusVoice)
                        {
                            if (!AudioTools.TryMonoToStereo(_decodedBuffer, ref pcmLength))
                            {
                                Log.Warning("MonoToStereo failed, using mono");
                                pcmLength = decoded.Length;
                            }
                        }

                        var bytes = new byte[pcmLength];
                        _decodedBuffer.AsSpan(0, pcmLength).CopyTo(bytes);

                        var packets = _clientPackets.GetOrAdd(realClientId, _ => new List<AudioPacket>());
                        lock (packets)
                        {
                            packets.Add(new AudioPacket
                            {
                                Data = bytes,
                                Timestamp = DateTime.UtcNow,
                                Sender = realClientId,
                                SenderName = clientName
                            });
                        }

                        // Обновляем/создаём таймер для этого клиента
                        var timer = _clientTimers.GetOrAdd(realClientId, id =>
                            new Timer(_ => Task.Run(() => SaveClientAsync(id)), null, Timeout.Infinite, Timeout.Infinite));
                        timer.Change(_speechTimeoutMs, Timeout.Infinite);

                        Log.Information($"[WHISPER] {clientName}({realClientId}): +{bytes.Length} bytes PCM ({codec})");
                    }
                    else
                    {
                        Log.Information($"[WHISPER] {clientName}({realClientId}): unsupported codec {codec}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"[WHISPER] {clientName}({sender}): decode failed, skipping packet");
            }
        }

        _nextPipe?.Write(data, meta);
    }

    /// <summary>
    /// Метод GetDecoder возвращает объект декодера Opus, связанный с определённым клиентом и кодеком.
    /// Если декодер для указанного ключа (клиента и кодека) ещё не существует, он создаётся.
    /// </summary>
    /// <param name="sender">Идентификатор клиента, для которого требуется декодер.</param>
    /// <param name="codec">Кодек, используемый для обработки аудиоданных.</param>
    /// <returns>Объект OpusDecoder, связанный с заданным клиентом и кодеком.</returns>
    private OpusDecoder GetDecoder(ClientId sender, Codec codec)
    {
        var key = (sender, codec);
        if (!_decoders.TryGetValue(key, out var decoder))
        {
            decoder = codec switch
            {
                Codec.OpusVoice => OpusDecoder.Create(48000, 1),
                Codec.OpusMusic => OpusDecoder.Create(48000, 2),
                _ => throw new NotSupportedException($"Codec {codec} not supported for decoding")
            };
            _decoders[key] = decoder;
        }
        return decoder;
    }

    /// <summary>
    /// Сохраняет аудиоданные клиента из временного буфера в формате PCM и конвертирует в MP3.
    /// Используется для обработки записей, связанных с клиентами TeamSpeak, а также удаления временных данных после сохранения.
    /// </summary>
    /// <param name="clientId">Идентификатор клиента, чьи аудиоданные необходимо сохранить.</param>
    /// <returns>Асинхронная задача для управления процессом выполнения операции.</returns>
    private async Task SaveClientAsync(ClientId clientId)
    {
        if (!_clientPackets.TryRemove(clientId, out var packets) || packets.Count == 0)
            return;

        string senderName;
        AudioPacket[] packetsToSave;
        lock (packets)
        {
            if (packets.Count == 0) return;
            packetsToSave = packets.ToArray();
            senderName = packetsToSave[0].SenderName;
            packets.Clear();
        }

        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var safeName = string.Join("_", senderName.Split(Path.GetInvalidFileNameChars()));
            var basePath = Path.Combine(_recordingsPath, $"whisper_{safeName}_{timestamp}");
            var rawPath = basePath + ".raw";
            var mp3Path = basePath + ".mp3";

            // Объединяем PCM пакеты (s16le 48000Hz Stereo)
            var combined = packetsToSave.SelectMany(p => p.Data).ToArray();
            await File.WriteAllBytesAsync(rawPath, combined);
            Log.Information($"[SAVED RAW] {rawPath} ({packetsToSave.Length} packets, {combined.Length} bytes)");

            // Конвертируем PCM raw в MP3 через ffmpeg
            await ConvertToMp3Async(rawPath, mp3Path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save recording");
        }
        finally
        {
            // Удаляем таймер, если он больше не нужен
            if (_clientTimers.TryRemove(clientId, out var timer))
            {
                timer.Dispose();
            }
        }
    }

    /// <summary>
    /// Асинхронный метод ConvertToMp3Async выполняет конвертацию аудиофайла формата PCM
    /// (s16le, 48000Hz, Stereo) в формат MP3 с использованием утилиты ffmpeg.
    /// </summary>
    /// <param name="inputPath">Полный путь к входному файлу в формате PCM.</param>
    /// <param name="mp3Path">Полный путь, по которому будет сохранен выходной MP3 файл.</param>
    /// <returns>Задача, представляющая собой процесс выполнения операции.</returns>
    private async Task ConvertToMp3Async(string inputPath, string mp3Path)
    {
        var ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory!, "ffmpeg.exe");
        if (!File.Exists(ffmpegPath))
        {
            Log.Warning("ffmpeg not found at {0}", ffmpegPath);
            return;
        }

        try
        {
            // На входе PCM s16le 48000Hz Stereo (после декодирования Opus -> PCM)
            var arguments = $"-hide_banner -loglevel error -f s16le -ar 48000 -ac 2 -i \"{inputPath}\" -ar 48000 -ac 1 -b:a 64k \"{mp3Path}\"";

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                Log.Warning("Failed to start ffmpeg");
                return;
            }

            var error = await process.StandardError.ReadToEndAsync();
            await Task.Run(process.WaitForExit);

            if (process.ExitCode == 0 && File.Exists(mp3Path))
            {
                File.Delete(inputPath);
                Log.Information($"[CONVERTED] {mp3Path}");
            }
            else
            {
                Log.Warning($"ffmpeg failed: {error}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ffmpeg error");
        }
    }

    public void Dispose()
    {
        foreach (var timer in _clientTimers.Values)
        {
            timer?.Dispose();
        }
        _clientTimers.Clear();

        if (_enabled)
        {
            foreach (var clientId in _clientPackets.Keys.ToArray())
            {
                SaveClientAsync(clientId).Wait();
            }
            foreach (var decoder in _decoders.Values)
            {
                decoder.Dispose();
            }
            _decoders.Clear();
        }
    }
}
