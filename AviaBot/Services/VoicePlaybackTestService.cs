using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AviaBot.Models;
using TSLib;
using TSLib.Audio;
using TSLib.Audio.Opus;
using TSLib.Full;

namespace AviaBot.Services;

/// <summary>
/// Фаза 2 (v5) — sample-accurate синхронное микширование.
///
/// Никаких jitter buffer'ов, очередей и race conditions.
/// Микшер сам дергает кадры у спикеров по глобальному таймлайну (seq).
/// Каждый спикер знает свой startSeq (задержку старта в тиках).
/// Микшер каждые 20 мс читает у каждого активного спикера кадр
/// с индексом [globalSeq - startSeq] и микширует.
///
/// Это даёт идеальную синхронизацию и позволяет проверить,
/// что само микширование (с эффектами) работает корректно.
/// </summary>
public class VoicePlaybackTestService : IDisposable
{
	private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<VoicePlaybackTestService>();

	private readonly TestVoicePlaybackSettings _settings;
	private readonly RelaySettings _relaySettings;
	private CancellationTokenSource? _cts;
	private Task? _playbackTask;
	private readonly ThreadLocal<Random> _random = new(() => new Random());

	public VoicePlaybackTestService(TestVoicePlaybackSettings settings, RelaySettings relaySettings)
	{
		_settings = settings;
		_relaySettings = relaySettings;
	}

	public void StartPlayback(TsFullClient tsClient)
	{
		if (!_settings.Enabled)
		{
			Log.Debug("VoicePlaybackTestService is disabled in config");
			return;
		}

		StopPlayback();

		_cts = new CancellationTokenSource();
		var token = _cts.Token;
		_playbackTask = Task.Run(() => PlayAsync(tsClient, token), token);
	}

	public void StopPlayback()
	{
		try
		{
			_cts?.Cancel();
			_playbackTask?.Wait(TimeSpan.FromSeconds(2));
		}
		catch { /* ignore */ }
		finally
		{
			_cts?.Dispose();
			_cts = null;
			_playbackTask = null;
		}
	}

	private async Task PlayAsync(TsFullClient tsClient, CancellationToken ct)
	{
		try
		{
			await Task.Delay(1000, ct);

			var targetId = await FindClientIdAsync(tsClient, _settings.TargetNickname, ct);
			if (targetId == default)
			{
				Log.Warning("Target user '{Nickname}' not found on TS3 server", _settings.TargetNickname);
				return;
			}

			Log.Information(
				"Starting SAMPLE-ACCURATE mixing test to '{Nickname}' ({ClientId})",
				_settings.TargetNickname, targetId);

			var pcm1 = await DecodeMp3Async(_settings.File1, ct);
			var pcm2 = await DecodeMp3Async(_settings.File2, ct);

			if (pcm1 == null || pcm2 == null)
			{
				Log.Warning("Failed to decode one or both MP3 files");
				return;
			}

			var frames1 = SplitIntoFrames(pcm1);
			var frames2 = SplitIntoFrames(pcm2);

			Log.Information(
				"Track1: {Count1} frames, Track2: {Count2} frames",
				frames1.Length, frames2.Length);

			// startSeq = через сколько тиков (20 мс) спикер начинает говорить
			var speaker1 = new Speaker((ClientId)1001, frames1, _settings.Distance1, startSeq: 0);
			var speaker2 = new Speaker((ClientId)1002, frames2, _settings.Distance2, startSeq: _settings.Track2DelayMs / 20);
			var speakers = new List<Speaker> { speaker1, speaker2 };

			using var encoder = OpusEncoder.Create(48000, 1, Application.Voip);
			await MixerLoopAsync(tsClient, speakers, encoder, targetId, ct);

			Log.Information("Sample-accurate mixing test completed");
		}
		catch (OperationCanceledException)
		{
			Log.Information("Sample-accurate mixing test cancelled");
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Sample-accurate mixing test error");
		}
	}

	/// <summary>
	/// Синхронный микшер по глобальному таймлайну.
	/// Каждый тик (20 мс) микшер запрашивает у каждого спикера
	/// кадр с индексом [globalSeq - startSeq]. Никаких очередей.
	/// </summary>
	private async Task MixerLoopAsync(
		TsFullClient tsClient,
		List<Speaker> speakers,
		OpusEncoder encoder,
		ClientId targetId,
		CancellationToken ct)
	{
		var encodeBuffer = new byte[4096];
		var recipientList = new[] { targetId };
		var sw = Stopwatch.StartNew();
		long startMs = sw.ElapsedMilliseconds;
		int globalSeq = 0;
		int mixedCount = 0;
		int silenceCount = 0;

		while (!ct.IsCancellationRequested)
		{
			// Точный тайминг: спим до момента globalSeq * 20 мс
			long targetMs = startMs + globalSeq * 20L;
			long now = sw.ElapsedMilliseconds;
			int sleep = (int)(targetMs - now);
			if (sleep > 0)
				await Task.Delay(sleep, ct);

			var mixed = new short[960];
			bool hasAny = false;
			bool allFinished = true;

			foreach (var speaker in speakers)
			{
				int localIdx = globalSeq - speaker.StartSeq;

				// Спикер еще не начал
				if (localIdx < 0)
				{
					allFinished = false;
					continue;
				}

				// Спикер уже закончил
				if (localIdx >= speaker.Frames.Length)
					continue;

				allFinished = false;

				// Берем кадр напрямую из массива
				var samples = DecodeFrame(speaker.Frames[localIdx]);
				if (!ApplySpeakerEffects(samples, speaker.Distance))
				{
					// За пределами MaxDistance — mute, не микшируем
					continue;
				}

				for (int i = 0; i < 960; i++)
				{
					int sum = mixed[i] + samples[i];
					mixed[i] = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
				}
				hasAny = true;
			}

			if (!hasAny && allFinished)
				break;

			if (hasAny)
				mixedCount++;
			else
				silenceCount++;

			// short[] → byte[]
			var pcmBytes = ArrayPool<byte>.Shared.Rent(960 * 2);
			for (int i = 0; i < 960; i++)
			{
				pcmBytes[i * 2] = (byte)(mixed[i] & 0xFF);
				pcmBytes[i * 2 + 1] = (byte)((mixed[i] >> 8) & 0xFF);
			}

			try
			{
				var encoded = encoder.Encode(
					pcmBytes.AsSpan(0, 960 * 2),
					encodeBuffer.Length,
					encodeBuffer.AsSpan());

				var packetId = tsClient.AllocateVoiceWhisperId();
				tsClient.SendAudioWhisper(
					encoded,
					Codec.OpusVoice,
					Array.Empty<ChannelId>(),
					recipientList,
					packetId);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(pcmBytes);
			}

			globalSeq++;

			// Лог каждые 5 сек
			if (globalSeq % 250 == 0)
			{
				Log.Debug("Mixer tick={Tick}, mixed={Mixed}, silence={Silence}",
					globalSeq, mixedCount, silenceCount);
			}
		}
	}

	private static short[] DecodeFrame(byte[] data)
	{
		var samples = new short[960];
		int len = Math.Min(data.Length / 2, 960);
		for (int i = 0; i < len; i++)
			samples[i] = (short)(data[i * 2] | (data[i * 2 + 1] << 8));
		return samples;
	}

	private bool ApplySpeakerEffects(short[] pcm, double distance)
	{
		var level = ResolveQualityLevel(distance);
		if (level == null)
			return false; // mute

		ApplyEffects(pcm, level);
		return true;
	}

	/// <summary>
	/// Спикер — пассивный источник. Не запускает Task.
	/// Микшер сам читает кадры по индексу.
	/// </summary>
	private class Speaker
	{
		public ClientId Id { get; }
		public byte[][] Frames { get; }
		public double Distance { get; }
		public int StartSeq { get; }

		public Speaker(ClientId id, byte[][] frames, double distance, int startSeq)
		{
			Id = id;
			Frames = frames;
			Distance = distance;
			StartSeq = startSeq;
		}
	}

	private static async Task<ClientId> FindClientIdAsync(
		TsFullClient tsClient, string nickname, CancellationToken ct)
	{
		for (int i = 0; i < 20; i++)
		{
			foreach (var kvp in tsClient.Book.Clients)
			{
				if (string.Equals(kvp.Value.Name, nickname, StringComparison.OrdinalIgnoreCase))
					return kvp.Key;
			}
			await Task.Delay(500, ct);
		}
		return default;
	}

	private async Task<byte[]?> DecodeMp3Async(string relativePath, CancellationToken ct)
	{
		var ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory!, "ffmpeg.exe");
		if (!File.Exists(ffmpegPath))
		{
			Log.Warning("ffmpeg not found at {Path}", ffmpegPath);
			return null;
		}

		var filePath = Path.GetFullPath(relativePath);
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
		if (process == null)
		{
			Log.Warning("Failed to start ffmpeg process");
			return null;
		}

		try
		{
			await using var stdout = process.StandardOutput.BaseStream;
			using var ms = new MemoryStream();
			await stdout.CopyToAsync(ms, ct);

			var error = await process.StandardError.ReadToEndAsync(ct);
			await process.WaitForExitAsync(ct);

			if (process.ExitCode != 0)
			{
				Log.Warning("ffmpeg decode failed for {File}: {Error}", filePath, error);
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

	private RadioQualityLevel? ResolveQualityLevel(double distance)
	{
		if (distance <= 0 || !_relaySettings.RadioEffectsEnabled)
			return null;

		if (_relaySettings.MaxDistance <= 0)
			return null;

		double factor = distance / _relaySettings.MaxDistance;

		// За пределами MaxDistance — полное заглушение (mute)
		if (factor > 1.0)
			return null;

		for (int i = 0; i < _relaySettings.RadioQualityLevels.Count; i++)
		{
			if (factor <= _relaySettings.RadioQualityLevels[i].MaxFactor)
				return _relaySettings.RadioQualityLevels[i];
		}

		return null;
	}

	private void ApplyEffects(short[] pcm, RadioQualityLevel level)
	{
		var rng = _random.Value!;
		for (int i = 0; i < pcm.Length; i++)
		{
			if (level.Dropout > 0 && rng.NextDouble() < level.Dropout)
			{
				pcm[i] = 0;
				continue;
			}

			short original = pcm[i];
			short sample = (short)(original * (1.0 - level.Attenuation));

			if (level.Noise > 0)
			{
				// Шум вычисляем от ОРИГИНАЛЬНОЙ амплитуды, чтобы при большом
				// attenuation шум всё равно был слышен (громче ослабленного сигнала)
				double originalAmp = Math.Abs((int)original);
				double noiseAmp = originalAmp * level.Noise * 1.2 + 20.0;
				double noise = (rng.NextDouble() * 2.0 - 1.0) * noiseAmp;
				sample = (short)Math.Clamp(sample + noise, short.MinValue, short.MaxValue);
			}

			if (level.CrushBits > 0)
			{
				sample = (short)((sample >> level.CrushBits) << level.CrushBits);
			}

			pcm[i] = sample;
		}
	}

	public void Dispose()
	{
		StopPlayback();
	}
}
