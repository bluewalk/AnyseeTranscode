using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .WriteTo.Console(EnvironmentExtensions.GetEnvironmentVariable("LOG_LEVEL", LogEventLevel.Information),
                    "{Timestamp:yyyy-MM-dd HH:mm:ss zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                    theme: AnsiConsoleTheme.Code
                ).CreateLogger();
            
            AppDomain.CurrentDomain.DomainUnload += (sender, eventArgs) => Log.CloseAndFlush();

            var builder = new HostBuilder()
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddOptions();

                    services.AddSingleton<IHostedService, Logic>();
                })
                .UseSerilog();

            await builder.RunConsoleAsync();
        }
    }
}
