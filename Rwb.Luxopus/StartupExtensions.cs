using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rwb.Luxopus.Jobs;
using Rwb.Luxopus.Services;
using System;

namespace Rwb.Luxopus
{
    public static class StartupExtensions
    {
        public static IHostBuilder AddAppsettingsWithAspNetCoreEnvironment(this IHostBuilder builder)
        {
            return builder.ConfigureAppConfiguration(cfg =>
            {
                cfg.AddJsonFile("appsettings.json");

                string? env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
                if (!string.IsNullOrEmpty(env))
                {
                    cfg.AddJsonFile($"appsettings.{env}.json", true);
                }
            });
        }

        public static IHostBuilder AddLuxopus(this IHostBuilder builder)
        {
            return builder.ConfigureServices((context, services) =>
                {
                    // NCrontab is a singleton.
                    services.AddScheduler(configureOptions =>
                    {
                        configureOptions.DateTimeKind = DateTimeKind.Local;
                    });

                    // Serivces.
                    services.Register<ILuxopusPlanService, LuxopusPlanService, LuxopusPlanSettings>(context);
                    services.Register<IInfluxQueryService, InfluxQueryService, InfluxDBSettings>(context);
                    services.Register<IInfluxWriterService, InfluxWriterService, InfluxDBSettings>(context);
                    services.Register<IEmailService, EmailService, EmailSettings>(context);
                    services.Register<ISmsService, SmsService, SmsSettings>(context);
                    services.Register<ILuxService, LuxService, LuxSettings>(context);
                    services.Register<IOctopusService, OctopusService, OctopusSettings>(context);
                    services.Register<ISolcastService, SolcastService, SolcastSettings>(context);
                    services.Register<ISunService, SunService, SunSettings>(context);
                    services.Register<IOpenWeathermapService, OpenWeathermapService, OpenWeathermapSettings>(context);

                    services.Register<IBatteryService, BatteryService, BatterySettings>(context);
                    //services.Register<IGenerationForecastService, GenerationForecastService, GenerationForecastSettings>(context);

                    // Main thingy.
                    services.AddScoped<Luxopus>();

                    // Jobs.
                    services.AddScoped<LuxMonitor>();
                    services.AddScoped<LuxDaily>();
                    services.AddScoped<OctopusMeters>();
                    services.AddScoped<OctopusPrices>();
                    services.AddScoped<Solcast>();
                    services.AddScoped<SolarPosition>();
                    services.AddScoped<Sunrise>();
                    services.AddScoped<Openweathermap>();

                    services.AddScoped<PlanChecker>();

                    //services.AddScoped<PlanZero>();
                    //services.AddScoped<PlanA>();
                    //services.AddScoped<PlanFlux1>();
                    services.AddScoped<PlanFlux2>();
                });
        }
    }
}
