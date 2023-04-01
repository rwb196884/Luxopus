using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using System;
using System.Text.Json;

namespace Rwb.Luxopus.Services
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

    public static class ServiceRegistrationExtensions
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

    //public static class DateTimeExensions
    //{
    //    /// <summary>
    //    /// https://stackoverflow.com/questions/19695439/get-the-default-timezone-for-a-country-via-cultureinfo
    //    /// </summary>
    //    /// <param name="longitude"></param>
    //    private static void GuessTimeZone(double longitude)
    //    {
    //        var zones = TzdbDateTimeZoneSource.Default.ZoneLocations.AsQueryable();
    //            zones = zones.OrderBy(o => Distance(o.Latitude, longitude, o.Latitude, o.Longitude, DistanceUnit.Kilometer));
    //        var bestZone = zones.FirstOrDefault();
    //        var dateTimeZone = TzdbDateTimeZoneSource.Default.ForId(bestZone.ZoneId);

    //        var newTime = DateTime.UtcNow.AddSeconds(dateTimeZone.MaxOffset.Seconds);

    //    }
    //    private enum DistanceUnit { StatuteMile, Kilometer, NauticalMile };
    //    private static double Distance(double lat1, double lon1, double lat2, double lon2, DistanceUnit unit)
    //    {
    //        double rlat1 = Math.PI * lat1 / 180;
    //        double rlat2 = Math.PI * lat2 / 180;
    //        double theta = lon1 - lon2;
    //        double rtheta = Math.PI * theta / 180;
    //        double dist =
    //            Math.Sin(rlat1) * Math.Sin(rlat2) + Math.Cos(rlat1) *
    //            Math.Cos(rlat2) * Math.Cos(rtheta);
    //        dist = Math.Acos(dist);
    //        dist = dist * 180 / Math.PI;
    //        dist = dist * 60 * 1.1515;

    //        switch (unit)
    //        {
    //            case DistanceUnit.Kilometer:
    //                return dist * 1.609344;
    //            case DistanceUnit.NauticalMile:
    //                return dist * 0.8684;
    //            default:
    //            case DistanceUnit.StatuteMile: //Miles
    //                return dist;
    //        }
    //    }
    //}

    public abstract class Settings { }

    public abstract class Service<T> where T : Settings
    {
        protected readonly T Settings;
        protected readonly ILogger Logger;

        protected Service(ILogger<Service<T>> logger, IOptions<T> settings)
        {
            Settings = settings.Value;
            Logger = logger;
        }

        public abstract bool ValidateSettings();

        protected static DateTime GetUtc(string timestamp, string timeZone)
        {
            DateTimeZone ntz = DateTimeZoneProviders.Tzdb[timeZone];
            //Offset o = ntz.GetUtcOffset();
            DateTime t = DateTime.Parse(timestamp);
            if( t.Kind != DateTimeKind.Utc)
            {
                return t.ToUniversalTime();
            }
            return t;

        }

        protected static DateTime GetUtc(JsonProperty e, string timeZone)
        {
            return GetUtc(e.Value.GetString(), timeZone);
        }
    }
}
