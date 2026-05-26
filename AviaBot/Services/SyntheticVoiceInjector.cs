using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AviaBot.Models;
using TSLib;
using TSLib.Audio;
using TSLib.Audio.Opus;
using TSLib.Full;
using TSLib.Full.Book;

namespace AviaBot.Services;

/// <summary>
/// Имитирует N одновременных спикеров, напрямую пишущих в VoiceRelayPipe.Write().
/// Каждый спикер — отдельный dedicated thread (как реальный TS3 клиент).
/// </summary>
public class SyntheticVoiceInjector : IDisposable
{
	private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<SyntheticVoiceInjector>();

	private readonly IAudioPassiveConsumer _voiceRelay;
	private readonly TsFullClient _tsClient;
	private readonly PlayerPositionService _positionService;
	private readonly int _speakerCount;
	private readonly bool _enabled;

	private CancellationTokenSource? _cts;
	private readonly List<Thread> _speakerThreads = new();
	private readonly List<SpeakerState> _speakers = new();

	public SyntheticVoiceInjector(
		IAudioPassiveConsumer voiceRelay,
		TsFullClient tsClient,
		PlayerPositionService positionService,
		int speakerCount,
		bool enabled)
	{
		_voiceRelay = voiceRelay;
		_tsClient = tsClient;
		_positionService = positionService;
		_speakerCount = speakerCount;
		_enabled = enabled;
	}

	public void Start()
	{
		if (!_enabled)
		{
			Log.Debug("SyntheticVoiceInjector is disabled");
			return;
		}

		Stop();
		_cts = new CancellationTokenSource();
		var token = _cts.Token;

		// Создаём фейковых спикеров
		var baseId = 9000;
		for (int i = 0; i < _speakerCount; i++)
		{
			var clientId = new ClientId((ushort)(baseId + i));
			var name = $"SyntheticSpeaker_{i:00}";

			var client = new Client { Name = name, Channel = ChannelId.Null };
			typeof(Client).GetProperty("Uid")?.SetValue(client, new Uid(name));
			_tsClient.Book.Clients[clientId] = client;

			var session = new PlayerSession
			{
				Id = baseId + i,
				Country = 101,
				GamerName = name,
				TeamSpeakId = name,
				ObjectName = "SynthPlane",
				TypeObject = "aircraft",
				X = 40000 + i * 500,
				Y = 150,
				Z = 100000 + i * 500,
				LastUpdate = DateTime.UtcNow,
				Locale = "ru"
			};
			_positionService.HandleSpawn(session);

			var speaker = new SpeakerState
			{
				ClientId = clientId,
				Name = name,
				PacketId = (ushort)(i * 1000)
			};
			_speakers.Add(speaker);

			var thread = new Thread(() => SpeakerLoop(speaker, token))
			{
				IsBackground = true,
				Priority = ThreadPriority.Normal,
				Name = $"SyntheticSpeaker-{i:00}"
			};
			_speakerThreads.Add(thread);
			thread.Start();
		}

		Log.Information("SyntheticVoiceInjector started with {Count} speakers (dedicated threads)", _speakers.Count);
	}

	public void Stop()
	{
		try
		{
			_cts?.Cancel();
			foreach (var t in _speakerThreads)
				t.Join(TimeSpan.FromSeconds(2));
		}
		catch { /* ignore */ }
		finally
		{
			_cts?.Dispose();
			_cts = null;
			_speakerThreads.Clear();
		}
	}

	private void SpeakerLoop(SpeakerState speaker, CancellationToken ct)
	{
		// Генерируем один Opus-фрейм тишины (48000, mono, 20ms = 960 samples)
		byte[] silentPcm = new byte[960 * 2];
		using var encoder = OpusEncoder.Create(48000, 1, Application.Voip);
		var encodeBuf = new byte[4096];
		var silentOpus = encoder.Encode(silentPcm.AsSpan(), encodeBuf.Length, encodeBuf.AsSpan());
		var silentFrame = silentOpus.ToArray();

		var sw = Stopwatch.StartNew();
		long startMs = sw.ElapsedMilliseconds;
		int tick = 0;

		while (!ct.IsCancellationRequested)
		{
			long targetMs = startMs + tick * 20L;
			long now = sw.ElapsedMilliseconds;
			int sleep = (int)(targetMs - now);
			if (sleep > 0)
				Thread.Sleep(sleep);

			var packet = BuildPacket(speaker, silentFrame);
			var meta = new Meta
			{
				In = new MetaIn
				{
					Whisper = true,
					Sender = speaker.ClientId
				}
			};
			_voiceRelay.Write(packet, meta);
			speaker.PacketId++;
			tick++;
		}
	}

	private static byte[] BuildPacket(SpeakerState speaker, byte[] opusPayload)
	{
		var packet = new byte[5 + opusPayload.Length];
		BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), speaker.PacketId);
		BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), speaker.ClientId.Value);
		packet[4] = (byte)Codec.OpusVoice;
		opusPayload.CopyTo(packet.AsSpan(5));
		return packet;
	}

	public void Dispose()
	{
		Stop();
		foreach (var speaker in _speakers)
		{
			_tsClient.Book.Clients.Remove(speaker.ClientId);
			_positionService.TryRemove(speaker.Name);
		}
		_speakers.Clear();
	}

	private class SpeakerState
	{
		public ClientId ClientId { get; set; }
		public string Name { get; set; } = "";
		public ushort PacketId { get; set; }
	}
}
