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