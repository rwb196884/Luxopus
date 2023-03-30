using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;

namespace Luxopus.Services
{
    public static class IServiceCollectionExtensions
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

    internal static class ServiceRegistrationExtensions
    {
        public static void Register<I, T, S>(this IServiceCollection services, HostBuilderContext context)
            where I : class
            where T : Service<S>, I
            where S : Settings
        {
            services.AddScoped<I, T>();
            services.ConfigureSettings<S>(context.Configuration);
        }
    }

    internal static class DateTimeExensions
    {
        public static long ToUnix(this DateTime t)
        {
            TimeSpan timeSpan = (t - new DateTime(1970, 1, 1, 0, 0, 0));
            return (long)timeSpan.TotalSeconds;
        }
    }

    internal abstract class Settings { }

    internal abstract class Service<T> where T : Settings
    {
        protected readonly T Settings;
        protected readonly ILogger Logger;

        protected Service(ILogger<Service<T>> logger, IOptions<T> settings)
        {
            Settings = settings.Value;
            Logger = logger;
        }

        public abstract bool ValidateSettings();
    }
}
