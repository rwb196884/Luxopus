using Rwb.Luxopus.Jobs;
using Rwb.Luxopus.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.IO;

namespace Rwb.Luxopus.Console
{
    public static class Program
    {
        static void Main(string[] args)
        {
            System.Console.WriteLine("-- Luxopus --");
            System.Console.WriteLine($"DOTNET_ENVIRONMENT = {Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")}");

            using (IHost host = Host.CreateDefaultBuilder(args)
                .ConfigureLogging((context, cfg) =>
                {
                    cfg.ClearProviders();
                    cfg.AddConfiguration(context.Configuration.GetSection("Logging"));
                    cfg.AddConsole();
                })
                .AddAppsettingsWithAspNetCoreEnvironment()
                .AddLuxopus()
                .Build()
                )

            {
                using (IServiceScope scope = host.Services.CreateScope())
                {
                    Job m = null;
                    //m = scope.ServiceProvider.GetRequiredService<LuxMonitor>();
                    //m = scope.ServiceProvider.GetRequiredService<LuxDaily>();
                    //m = scope.ServiceProvider.GetRequiredService<OctopusMeters>();
                    //m = scope.ServiceProvider.GetRequiredService<OctopusPrices>();
                    //m = scope.ServiceProvider.GetRequiredService<Solcast>();
                    m = scope.ServiceProvider.GetRequiredService<PlanA>();
                    m.RunAsync(CancellationToken.None).Wait();
                    return;

                    Luxopus l = scope.ServiceProvider.GetRequiredService<Luxopus>();
                    l.Start();

                    System.Console.WriteLine("Started. Press <enter> to stop.");
                    System.Console.ReadLine();
                    System.Console.WriteLine("Stopping...");
                    l.Stop();
                }
            }
            System.Console.WriteLine("Done.");
        }
    }
}