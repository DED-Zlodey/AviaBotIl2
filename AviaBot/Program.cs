using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using AviaBot.Models;
using AviaBot.Services;

namespace AviaBot
{
    class Program
    {
        static async Task Main(string[] args)
        {
            SetupLogger();

            try
            {
                // libopus
                bool loaded = TSLib.Audio.Opus.NativeMethods.PreloadLibrary();
                if (!loaded)
                    Log.Warning("Couldn't find libopus. Make sure it is installed or placed in the correct folder.");
                else
                    Log.Information("libopus loaded: {OpusInfo}", TSLib.Audio.Opus.NativeMethods.Info);

                var host = Host.CreateDefaultBuilder(args)
                    .UseSerilog()
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
                        services.Configure<RecordingSettings>(ctx.Configuration.GetSection("Recording"));
                        services.Configure<TestVoicePlaybackSettings>(ctx.Configuration.GetSection("TestVoicePlayback"));

                        services.AddSingleton<PlayerPositionService>();
                        services.AddSingleton(sp => sp.GetRequiredService<IOptions<TestVoicePlaybackSettings>>().Value);
                        services.AddSingleton<VoicePlaybackTestService>();
                        services.AddSingleton<Ts3BotService>();
                        services.AddSingleton(sp => sp.GetRequiredService<IOptions<RelaySettings>>().Value);
                        services.AddSingleton<RelayService>();
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
                Log.CloseAndFlush();
            }
        }

        static void SetupLogger()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .MinimumLevel.Override("AviaBot.Services", LogEventLevel.Debug)
                .WriteTo.Console(
                    outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.Seq("http://localhost:5341")
                .CreateLogger();
        }
    }
}
