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
class Program
{
	[DllImport("winmm.dll")]
	static extern uint timeBeginPeriod(uint uPeriod);

	[DllImport("winmm.dll")]
	static extern uint timeEndPeriod(uint uPeriod);

	static async Task Main(string[] args)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			timeBeginPeriod(1);

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
				timeEndPeriod(1);
			await Log.CloseAndFlushAsync();
		}
	}

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

public class Ts3Settings
{
	public string Address { get; set; } = "localhost:9987";
	public string ServerPassword { get; set; } = "";
	public string Channel { get; set; } = "";
	public string ChannelPassword { get; set; } = "";
	public int SecurityLevel { get; set; } = -1;
}

public class TestSettings
{
	public string TargetNickname { get; set; } = "Dispatcher";
	public string Name { get; set; } = "TestPilot1";
	public string FilePath { get; set; } = "test1.mp3";
}

public class TestUserRunner : IDisposable
{
	private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<TestUserRunner>();

	private readonly Ts3Settings _ts3Settings;
	private readonly TestSettings _testSettings;
	private BotInstance? _bot;
	private CancellationTokenSource? _cts;
	private Task? _playbackTask;
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

	private class BotInstance
	{
		public string Name { get; set; } = "";
		public string FilePath { get; set; } = "";
		public TsFullClient Client { get; set; } = null!;
		public DedicatedTaskScheduler Scheduler { get; set; } = null!;
	}

	private class SavedIdentity
	{
		public string PrivateKey { get; set; } = "";
		public ulong Offset { get; set; }
	}
}
