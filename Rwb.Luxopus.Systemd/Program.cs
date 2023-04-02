using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;

namespace Rwb.Luxopus.Systemd
{
    public class Program
    {
        public static void Main(string[] args)
        {
            IHost host = Host.CreateDefaultBuilder(args)
                .UseSystemd()
                .ConfigureAppConfiguration(cfg =>
                {
                    cfg.AddJsonFile("appsettings.json");

                    string? env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
                    if (!string.IsNullOrEmpty(env))
                    {
                        cfg.AddJsonFile($"appsettings.{env}.json", true);
                    }

                    cfg.AddJsonFile("/etc/luxopus.config", true);
                })
                .ConfigureServices(services =>
                {
                    services.AddHostedService<Worker>();
                })
                .Build();

            host.Run();
        }
    }
}