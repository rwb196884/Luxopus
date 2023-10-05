using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;

namespace Rwb.Luxopus.Experiments
{
    internal class Program
    {
        static void Main(string[] args)
        {
            System.Console.WriteLine("-- Rwb.Luxopus.Experiments --");
            System.Console.WriteLine($"DOTNET_ENVIRONMENT = {Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")}");

            using (IHost host = Host.CreateDefaultBuilder(args)
                .ConfigureLogging((context, cfg) =>
                {
                    cfg.ClearProviders();
                    cfg.AddConfiguration(context.Configuration.GetSection("Logging"));
                    cfg.AddSimpleConsole(configure =>
                    {
                        configure.IncludeScopes = true;
                        configure.SingleLine = true;
                        configure.TimestampFormat = "dd MMM HH:mm ";
                    });
                })
                .AddAppsettingsWithAspNetCoreEnvironment()
                .AddLuxopus()
                .ConfigureServices((context, services) => { services.AddScoped<Experiment>(); })
                    .Build()
                    )

            {
                using (IServiceScope scope = host.Services.CreateScope())
                {
                    Experiment e = scope.ServiceProvider.GetRequiredService<Experiment>();
                    e.RunAsync().Wait();
                }
            }
            System.Console.WriteLine("Done.");
        }
    }
}