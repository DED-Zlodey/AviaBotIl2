using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using AviaBot.Enums;
using AviaBot.Models;
using TSLib;
using TSLib.Audio;
using TSLib.Audio.Opus;
using TSLib.Full;

namespace AviaBot.Services;

/// <summary>
/// Голосовой ретранслятор с эффектами по дальности.
///
/// Архитектура:
///   Write()       → decode Opus → PCM → enqueue в worker thread
///   WorkerLoop()  → blocking Take() → group by level → effects → encode → send
///
/// Passthrough (effects off): zero-copy, без decode/encode, без очереди.
/// </summary>
public class VoiceRelayPipe : IAudioPassiveConsumer, IDisposable
{
	private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<VoiceRelayPipe>();

	private readonly TsFullClient _tsClient;
	private readonly PlayerPositionService _positionService;
	private readonly RelaySettings _settings;
	private IAudioPassiveConsumer? _nextPipe;

	private readonly ConcurrentDictionary<(ClientId, Codec), OpusDecoder> _decoders = new();
	private readonly ConcurrentDictionary<(ClientId, int), OpusEncoder> _encoders = new();

	private readonly ConcurrentDictionary<string, ClientId> _clientIdCache = new();
	private readonly ConcurrentDictionary<ClientId, PlayerPosition?> _positionCache = new();
	private DateTime _lastCacheRefresh = DateTime.MinValue;
	private DateTime _lastPositionCacheRefresh = DateTime.MinValue;

	private readonly ThreadLocal<Random> _random = new(() => new Random());

	private readonly CancellationTokenSource _cts = new();
	private readonly BlockingCollection<FrameItem> _frameQueue = new();
	private readonly Thread _workerThread;

	public IAudioPassiveConsumer? OutStream
	{
		get => _nextPipe;
		set => _nextPipe = value;
	}

	public bool Active => true;

	public VoiceRelayPipe(TsFullClient tsClient, PlayerPositionService positionService, RelaySettings settings)
	{
		_tsClient = tsClient;
		_positionService = positionService;
		_settings = settings;

		_workerThread = new Thread(WorkerLoop)
		{
			IsBackground = true,
			Priority = ThreadPriority.Normal,
			Name = "VoiceRelayWorker"
		};
		_workerThread.Start();
	}

	public void Write(Span<byte> data, Meta? meta)
	{
		_nextPipe?.Write(data, meta);

		if (data.Length < 6)
			return;

		var realClientId = (ClientId)BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
		var codec = (Codec)data[4];

		if (!_tsClient.Book.Clients.TryGetValue(realClientId, out var client))
			return;

		var senderName = client.Name;
		if (string.IsNullOrEmpty(senderName))
			return;

		if (!(meta?.In.Whisper ?? false))
			return;

		if (!_positionService.TryGetByName(senderName, out var sender) || sender == null)
			return;

		if (sender.Coalition != 101 && sender.Coalition != 201)
			return;

		var audioData = data.Slice(5);
		if (audioData.Length < 3)
			return;

		if (!_settings.RadioEffectsEnabled)
		{
			// Fast passthrough: zero-copy, no decode/encode, no worker queue
			RefreshClientCacheIfNeeded();
			var recipients = ResolveRecipients(sender);
			if (recipients.Count > 0)
			{
				_tsClient.SendAudioWhisper(
					audioData, codec,
					Array.Empty<ChannelId>(),
					recipients);
			}
			return;
		}

		// Decode Opus → PCM → enqueue to worker
		int channels = codec == Codec.OpusVoice ? 1 : 2;
		int chunkBytes = channels * 960 * 2; // 20 мс
		int decodeSize = chunkBytes * 3;     // буфер до 60 мс
		var decodedBuffer = ArrayPool<byte>.Shared.Rent(decodeSize);
		var opusTemp = ArrayPool<byte>.Shared.Rent(audioData.Length);

		try
		{
			audioData.CopyTo(opusTemp);
			var decoder = _decoders.GetOrAdd((realClientId, codec), _ =>
				OpusDecoder.Create(48000, codec == Codec.OpusVoice ? 1 : 2));

			ReadOnlySpan<byte> decodedPcm;
			try
			{
				decodedPcm = decoder.Decode(
					opusTemp.AsSpan(0, audioData.Length),
					decodedBuffer.AsSpan(0, decodeSize));
			}
			catch (Exception ex)
			{
				Log.Warning(ex, "VoiceRelay: decode failed for {Name}", senderName);
				return;
			}

			int sampleCount = decodedPcm.Length / 2;
			int frameSamples = channels * 960;
			if (sampleCount == 0) return;
			if (sampleCount % frameSamples != 0)
			{
				Log.Warning("VoiceRelay: decoded PCM {Length} not multiple of 20ms frame", decodedPcm.Length);
			}
			int numChunks = sampleCount / frameSamples;
			if (numChunks == 0) return;

			for (int chunk = 0; chunk < numChunks; chunk++)
			{
				var chunkPcm = new short[frameSamples];
				var chunkSpan = decodedPcm.Slice(chunk * frameSamples * 2, frameSamples * 2);
				for (int i = 0; i < frameSamples; i++)
					chunkPcm[i] = (short)(chunkSpan[i * 2] | (chunkSpan[i * 2 + 1] << 8));

				try
				{
					_frameQueue.Add(new FrameItem(realClientId, codec, chunkPcm), _cts.Token);
				}
				catch (OperationCanceledException)
				{
					break;
				}
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(decodedBuffer);
			ArrayPool<byte>.Shared.Return(opusTemp);
		}
	}

	private void WorkerLoop()
	{
		while (!_cts.IsCancellationRequested)
		{
			try
			{
				var item = _frameQueue.Take(_cts.Token);
				ProcessFrameItem(item);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				Log.Error(ex, "VoiceRelay: worker loop exception");
			}
		}
	}

	private void ProcessFrameItem(FrameItem item)
	{
		try
		{
			var senderName = GetClientName(item.SpeakerId);
			if (string.IsNullOrEmpty(senderName)) return;
			if (!_positionService.TryGetByName(senderName, out var sender) || sender == null) return;

			var groups = ResolveGroups(sender);
			if (groups.Count == 0) return;

			ushort packetId = _tsClient.AllocateVoiceWhisperId();
			foreach (var (levelIdx, recipients) in groups)
			{
				var level = _settings.RadioQualityLevels[levelIdx];
				short[] effectedPcm;

				if (!_settings.RadioEffectsEnabled
				    || sender.IsInLobby
				    || sender.Category == CategoryObject.Spectator)
				{
					effectedPcm = item.Pcm;
				}
				else
				{
					effectedPcm = ArrayPool<short>.Shared.Rent(item.Pcm.Length);
					double noiseState = 0.0;
					ApplyEffectsToChunk(item.Pcm, level, effectedPcm, ref noiseState);
				}

				var pcmBytes = ArrayPool<byte>.Shared.Rent(item.Pcm.Length * 2);
				try
				{
					for (int i = 0; i < item.Pcm.Length; i++)
					{
						pcmBytes[i * 2]     = (byte)(effectedPcm[i] & 0xFF);
						pcmBytes[i * 2 + 1] = (byte)((effectedPcm[i] >> 8) & 0xFF);
					}

					var encoder = _encoders.GetOrAdd((item.SpeakerId, levelIdx), _ =>
						OpusEncoder.Create(48000,
							item.Codec == Codec.OpusVoice ? 1 : 2,
							item.Codec == Codec.OpusVoice ? Application.Voip : Application.Audio));

					var encodeBuf = ArrayPool<byte>.Shared.Rent(4096);
					try
					{
						Span<byte> encoded;
						lock (encoder)
						{
							encoded = encoder.Encode(
								pcmBytes.AsSpan(0, item.Pcm.Length * 2),
								encodeBuf.Length,
								encodeBuf.AsSpan());
						}

						if (encoded.Length > 0)
						{
							_tsClient.SendAudioWhisper(
								encoded, item.Codec,
								Array.Empty<ChannelId>(),
								recipients,
								packetId);
						}
					}
					finally
					{
						ArrayPool<byte>.Shared.Return(encodeBuf);
					}
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(pcmBytes);
					if (effectedPcm != item.Pcm)
						ArrayPool<short>.Shared.Return(effectedPcm);
				}
			}
		}
		catch (Exception ex)
		{
			Log.Warning(ex, "VoiceRelay: process frame item failed for {Speaker}", item.SpeakerId);
		}
	}

	private void ApplyEffectsToChunk(ReadOnlySpan<short> pcm, RadioQualityLevel level, Span<short> output, ref double noiseState)
	{
		// WorkerLoop is single-threaded, ThreadLocal gives us one rng per thread
		var rng = _random.Value!;
		const double alpha = 0.92; // one-pole low-pass, ~600 Hz @ 48 kHz

		for (int i = 0; i < pcm.Length; i++)
		{
			short original = pcm[i];
			short sample = (short)(original * (1.0 - level.Attenuation));

			if (level.Noise > 0)
			{
				// Distance-based absolute hiss: further = louder background noise,
				// independent of instantaneous voice amplitude.
				// Low-pass filtered triangular noise -> softer, "lower" sounding static.
				double noiseAmp = level.Noise * 6265.0 * level.Attenuation + 1.8;
				double white = rng.NextDouble() + rng.NextDouble() - 1.0; // triangular [-1,1]
				noiseState = noiseState * alpha + white * (1.0 - alpha);
				double noise = noiseState * noiseAmp;
				sample = (short)Math.Clamp(sample + noise, short.MinValue, short.MaxValue);
			}

			output[i] = sample;
		}
	}

	private string? GetClientName(ClientId id)
	{
		foreach (var kvp in _tsClient.Book.Clients)
			if (kvp.Key == id) return kvp.Value.Name;
		return null;
	}

	private List<ClientId> ResolveRecipients(PlayerPosition sender)
	{
		var result = new List<ClientId>();
		IEnumerable<PlayerPosition> candidates = sender.IsInLobby || sender.Category == CategoryObject.Spectator
			? _positionService.GetLobbyRecipients(sender.Coalition)
			: _positionService.GetInSphere(sender.Coalition, sender.X, sender.Y, sender.Z, _settings.MaxDistance);

		foreach (var player in candidates)
		{
			if (player.GamerName == sender.GamerName) continue;
			if (_settings.CoalitionCheck && player.Coalition != sender.Coalition) continue;
			if (TryGetClientIdByName(player.GamerName, out var recipientId))
				result.Add(recipientId);
		}
		return result;
	}

	private Dictionary<int, List<ClientId>> ResolveGroups(PlayerPosition sender)
	{
		var groups = new Dictionary<int, List<ClientId>>();
		RefreshPositionCache();
		RefreshClientCacheIfNeeded();

		IEnumerable<PlayerPosition> candidates = sender.IsInLobby || sender.Category == CategoryObject.Spectator
			? _positionService.GetLobbyRecipients(sender.Coalition)
			: _positionService.GetInSphere(sender.Coalition, sender.X, sender.Y, sender.Z, _settings.MaxDistance);

		foreach (var player in candidates)
		{
			if (player.GamerName == sender.GamerName) continue;
			if (_settings.CoalitionCheck && player.Coalition != sender.Coalition) continue;
			if (!TryGetClientIdByName(player.GamerName, out var recipientId)) continue;

			double distance = GetDistance(sender, recipientId);
			double factor = distance / _settings.MaxDistance;
			int? levelIdx = GetQualityLevelIndex(factor);
			if (levelIdx == null) continue;

			if (!groups.TryGetValue(levelIdx.Value, out var list))
			{
				list = new List<ClientId>();
				groups[levelIdx.Value] = list;
			}
			list.Add(recipientId);
		}

		return groups;
	}

	private int? GetQualityLevelIndex(double factor)
	{
		for (int i = 0; i < _settings.RadioQualityLevels.Count; i++)
			if (factor <= _settings.RadioQualityLevels[i].MaxFactor)
				return i;
		return null;
	}

	private void RefreshPositionCache()
	{
		var now = DateTime.UtcNow;
		if (now - _lastPositionCacheRefresh < TimeSpan.FromSeconds(1)) return;
		_positionCache.Clear();
		foreach (var kvp in _tsClient.Book.Clients)
			if (_positionService.TryGetByName(kvp.Value.Name, out var pos))
				_positionCache[kvp.Key] = pos;
		_lastPositionCacheRefresh = now;
	}

	private double GetDistance(PlayerPosition sender, ClientId recipientId)
	{
		if (!_positionCache.TryGetValue(recipientId, out var pos) || pos == null)
			return double.MaxValue;
		double dx = sender.X - pos.X;
		double dy = sender.Y - pos.Y;
		double dz = sender.Z - pos.Z;
		return Math.Sqrt(dx * dx + dy * dy + dz * dz);
	}

	private bool TryGetClientIdByName(string name, out ClientId clientId)
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

	private void RefreshClientCacheIfNeeded()
	{
		var now = DateTime.UtcNow;
		if (now - _lastCacheRefresh < TimeSpan.FromSeconds(5)) return;
		_clientIdCache.Clear();
		foreach (var kvp in _tsClient.Book.Clients)
			if (!string.IsNullOrEmpty(kvp.Value.Name))
				_clientIdCache[kvp.Value.Name] = kvp.Key;
		_lastCacheRefresh = now;
	}

	public void Dispose()
	{
		_cts.Cancel();
		_frameQueue.CompleteAdding();
		_workerThread.Join(TimeSpan.FromSeconds(2));
		foreach (var d in _decoders.Values) d.Dispose();
		_decoders.Clear();
		foreach (var e in _encoders.Values) e.Dispose();
		_encoders.Clear();
	}

	private readonly record struct FrameItem(
		ClientId SpeakerId,
		Codec Codec,
		short[] Pcm);
}
