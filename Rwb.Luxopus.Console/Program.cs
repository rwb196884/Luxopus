using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Jobs;
using System;
using System.Threading;

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
                    cfg.AddConsole(configure =>
                    {
                        configure.TimestampFormat = "dd MMM HH:mm";
                    });
                })
                .AddAppsettingsWithAspNetCoreEnvironment()
                .AddLuxopus()
                .Build()
                )

            {
                using (IServiceScope scope = host.Services.CreateScope())
                {
                    Job m = null;
                    //m = scope.ServiceProvider.GetRequiredService<Batt>();
                    //m = scope.ServiceProvider.GetRequiredService<LuxMonitor>();
                    //m = scope.ServiceProvider.GetRequiredService<LuxDaily>();
                    //m = scope.ServiceProvider.GetRequiredService<OctopusMeters>();
                    //m = scope.ServiceProvider.GetRequiredService<OctopusPrices>();
                    //m = scope.ServiceProvider.GetRequiredService<Solcast>();
                    //m = scope.ServiceProvider.GetRequiredService<PlanA>();
                    //m = scope.ServiceProvider.GetRequiredService<PlanFlux>();
                    //m = scope.ServiceProvider.GetRequiredService<PlanChecker>();
                    //m.RunAsync(CancellationToken.None).Wait();
                    //return;

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