using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using AviaBot.Models;
using AviaBot.Services;

namespace AviaBot;

/// <summary>
/// Основной класс программы, содержащий точку входа для выполнения.
/// </summary>
static class Program
{
	[DllImport("winmm.dll")]
	static extern uint timeBeginPeriod(uint uPeriod);

	[DllImport("winmm.dll")]
	static extern uint timeEndPeriod(uint uPeriod);

	/// <summary>
	/// Точка входа программы, отвечает за настройку и запуск основного хоста приложения.
	/// </summary>
	/// <param name="args">Аргументы командной строки, переданные при запуске приложения.</param>
	/// <remarks>
	/// Метод выполняет предварительную настройку, включая установку разрешения таймера (на Windows),
	/// загрузку библиотеки libopus, настройку логгирования через Serilog из конфигурации,
	/// а также конфигурацию и запуск хоста приложения.
	/// В случае возникновения исключения, оно логируется как фатальная ошибка.
	/// </remarks>
	/// <return>Задача, представляющая асинхронное выполнение программы.</return>
	static async Task Main(string[] args)
	{
		var configuration = new ConfigurationBuilder()
			.SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
			.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
			.Build();

		Log.Logger = new LoggerConfiguration()
			.ReadFrom.Configuration(configuration)
			.CreateLogger();

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			timeBeginPeriod(1);
			Log.Debug("Timer resolution set to 1ms");
		}

		try
		{
			bool loaded = TSLib.Audio.Opus.NativeMethods.PreloadLibrary();
			if (!loaded)
				Log.Warning("Couldn't find libopus. Make sure it is installed or placed in the correct folder.");
			else
				Log.Information("libopus loaded: {OpusInfo}", TSLib.Audio.Opus.NativeMethods.Info);

			var host = Host.CreateDefaultBuilder(args)
				.UseSerilog((context, services, loggerConfiguration) =>
					loggerConfiguration.ReadFrom.Configuration(context.Configuration))
				.ConfigureAppConfiguration((ctx, config) =>
				{
					config.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
					config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
				})
				.ConfigureServices((ctx, services) =>
				{
					services.Configure<Ts3Settings>(ctx.Configuration.GetSection("Ts3"));
					services.Configure<RabbitMqSettings>(ctx.Configuration.GetSection("RabbitMq"));
					services.Configure<RelaySettings>(ctx.Configuration.GetSection("Relay"));
					services.Configure<TestVoicePlaybackSettings>(ctx.Configuration.GetSection("TestVoicePlayback"));

					services.AddSingleton<PlayerPositionService>();
					services.AddSingleton(sp => sp.GetRequiredService<IOptions<TestVoicePlaybackSettings>>().Value);
					services.AddSingleton<Ts3BotService>();
					services.AddSingleton(sp => sp.GetRequiredService<IOptions<RelaySettings>>().Value);
					services.AddHostedService<RabbitMqConsumer>();
					services.AddHostedService(sp => sp.GetRequiredService<Ts3BotService>());
				})
				.UseConsoleLifetime()
				.Build();

			await host.RunAsync();
		}
		catch (Exception ex)
		{
			Log.Fatal(ex, "Fatal error");
			throw;
		}
		finally
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				timeEndPeriod(1);
			await Log.CloseAndFlushAsync();
		}
	}
}
