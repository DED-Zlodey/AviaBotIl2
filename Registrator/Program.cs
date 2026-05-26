using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Registrator.Data;
using Registrator.Models;
using Registrator.Services;
using Serilog;

namespace Registrator
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();

            try
            {
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
                        services.Configure<RegistratorSettings>(ctx.Configuration.GetSection("Ts3"));
                        services.AddDbContextFactory<AppDbContext>(options =>
                            options.UseNpgsql(ctx.Configuration.GetConnectionString("Default")));
                        services.AddHostedService<Ts3RegistratorService>();
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
    }
}
