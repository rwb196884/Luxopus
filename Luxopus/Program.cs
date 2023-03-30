﻿using Luxopus.Jobs;
using Luxopus.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;

namespace Luxopus
{
    public static class Program
    {
        static void Main(string[] args)
        {
            using (IHost host = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration(cfg =>
                {
                    cfg.AddJsonFile("appsettings.json");

                    string? env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
                    if (!string.IsNullOrEmpty(env))
                    {
                        cfg.AddJsonFile($"appsettings.{env}.json", true);
                    }
                })
                .ConfigureServices((context, services) =>
                {
                    // NCrontab is a singleton.
                    services.AddScheduler();

                    // Serivces.
                    services.Register<ILuxopusPlanService, LuxopusPlanService, LuxopusPlanSettings>(context);
                    services.Register<IInfluxQueryService, InfluxQueryService, InfluxDBSettings>(context);
                    services.Register<IInfluxWriterService, InfluxWriterService, InfluxDBSettings>(context);
                    services.Register<IEmailService, EmailService, EmailSettings>(context);
                    services.Register<ILuxService, LuxService, LuxSettings>(context);

                    // Main thingy.
                    services.AddScoped<Luxopus>();

                    // Jobs.
                    services.AddScoped<LuxMonitor>();

                })
                .ConfigureLogging((context, cfg) =>
                {
                    cfg.ClearProviders();
                    cfg.AddConfiguration(context.Configuration.GetSection("Logging"));
                    cfg.AddConsole();
                })
                .Build()
                )

            {
                using (IServiceScope scope = host.Services.CreateScope())
                {
                    Luxopus l = scope.ServiceProvider.GetRequiredService<Luxopus>();
                    l.Start();

                    Console.WriteLine("Started. Press <enter> to stop.");
                    Console.ReadLine();
                    Console.WriteLine("Stopping...");
                    l.Stop();
                }
            }
            Console.WriteLine("Done.");
        }
    }
}