using InfluxDB.Client;
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

                    string? env = Environment.GetEnvironmentVariable("ASPNETCURE_ENVIRONMENT");
                    if (!string.IsNullOrEmpty(env))
                    {
                        cfg.AddJsonFile($"appsettings.{env}.json", true);
                    }
                })
                .ConfigureServices((context, services) =>
                {
                    services.AddScoped<ILuxopusPlanService, LuxopusPlanService>();
                    services.AddScoped<Luxopus>();
                    services.AddSingleton<InfluxQueryService>();

                    services.ConfigureSettings<LuxopusSettings>(context.Configuration);
                    services.ConfigureSettings<LuxSettings>(context.Configuration);
                    services.ConfigureSettings<InfluxDBSettings>(context.Configuration);

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
                    l.RunAsync().Wait();
                }
            }
            Console.WriteLine("Done.");
        }
    }

    public static partial class IServiceCollectionExtensions
    {
        /// <summary>
        /// Make <see cref="Microsoft.Extensions.Options.IOptions<typeparamref name="T"/>"/> available for
        /// dependency injection. The associated configuration section must be called <typeparamref name="T"/>Settings.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        /// <exception cref="NullReferenceException"></exception>
        public static IServiceCollection ConfigureSettings<T>(this IServiceCollection services, IConfiguration configuration) where T : class
        {
            string className = typeof(T).Name;
            if (!className.EndsWith("Settings"))
            {
                throw new Exception($"Cannot configure appsettings section for class {className} because the class name does not end with 'Settings'.");
            }

            string configurationSectionName = className.Substring(0, className.Length - "Settings".Length);
            IConfigurationSection configSection = configuration.GetSection(configurationSectionName);
            if (configSection == null) // configSection.Value is not populated at this point! https://stackoverflow.com/questions/46017593/configuration-getsection-always-returns-value-property-null
            {
                throw new NullReferenceException($"Configuration section named {configurationSectionName} (to bind to class {className}) is required but got null.");
            }

            services.Configure<T>(configSection);
            return services;
        }
    }
}