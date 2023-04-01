using Rwb.Luxopus.Jobs;
using Rwb.Luxopus.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;

namespace Rwb.Luxopus.Console
{
    public static class Program
    {
        static void Main(string[] args)
        {
            // MS handles timezones incorrectly: London is always GMT.
            // Therefore we use NodaTime instead.

            /*
            DateTimeZone ntz = DateTimeZoneProviders.Tzdb["Europe/London"];

            DateTime tu = DateTime.UtcNow;
            DateTime tl = DateTime.Now;
            DateTime tO = DateTime.Parse("2023-03-03T15:55:00+0100");

            Instant iu = Instant.FromDateTimeUtc(tu);
            Instant il = Instant.FromDateTimeOffset(tl);
            Instant iO = Instant.FromDateTimeOffset(tO);

            ZonedDateTime ltu = iu.InZone(ntz);
            ZonedDateTime lt1 = il.InZone(ntz);
            ZonedDateTime ltO = iO.InZone(ntz);

            string su = tu.ToString("yyyy-MM-ddTHH:MM:sszzz");
            string sl = tl.ToString("yyyy-MM-ddTHH:MM:sszzz");

            DateTime u = tO.ToUniversalTime();
            */

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
                    services.Register<IOctopusService, OctopusService, OctopusSettings>(context);
                    services.Register<ISolcastService, SolcastService, SolcastSettings>(context);

                    // Main thingy.
                    services.AddScoped<Luxopus>();

                    // Jobs.
                    services.AddScoped<LuxMonitor>();
                    services.AddScoped<LuxDaily>();
                    services.AddScoped<OctopusMeters>();
                    services.AddScoped<OctopusPrices>();
                    services.AddScoped<Solcast>();

                    services.AddScoped<PlanA>();

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