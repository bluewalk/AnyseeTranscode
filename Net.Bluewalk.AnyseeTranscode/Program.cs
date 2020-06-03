using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Net.Bluewalk.DotNetEnvironmentExtensions;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace Net.Bluewalk.AnyseeTranscode
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var version = FileVersionInfo.GetVersionInfo(typeof(Program).Assembly.Location).ProductVersion;
            Console.WriteLine($"Anysee Transcode {version}");
            Console.WriteLine("https://github.com/bluewalk/AnyseeTranscode\n");

            Log.Logger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Debug()
                .WriteTo.Console(
                    EnvironmentExtensions.GetEnvironmentVariable("LOG_LEVEL", LogEventLevel.Information),
                    "{Timestamp:yyyy-MM-dd HH:mm:ss zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                    theme: AnsiConsoleTheme.Code
                ).CreateLogger();
            
            AppDomain.CurrentDomain.DomainUnload += (sender, eventArgs) => Log.CloseAndFlush();

            var builder = new HostBuilder()
                .ConfigureAppConfiguration((hostingContext, config) =>
                {
                    config.AddEnvironmentVariables();

                    if (File.Exists("config.json"))
                        config.AddJsonFile("config.json", false, true);

                    if (args != null)
                    {
                        config.AddCommandLine(args);
                    }
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddOptions();

                    //services.Replace(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(DateTimeLogger<>)));

                    //services.Configure<Config>(hostContext.Configuration.GetSection("Config"));

                    services.AddSingleton<IHostedService, Logic>();
                })
                .ConfigureLogging((hostingContext, logging) => {
                    logging.AddConfiguration(hostingContext.Configuration.GetSection("Logging"));
                    logging.AddConsole();
                });

            await builder.RunConsoleAsync();
        }
    }
}
