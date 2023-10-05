using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Services
{
    public class OpenWeathermapSettings : Settings
    {
        public decimal Latitude { get; set; }
        public decimal Longitude { get; set; }
        public string ApiKey { get; set; }
    }

    /// <summary>
    /// https://openweathermap.org/weather-conditions
    /// </summary>
    public enum WeatherDescription
    {
        /// <summary>
        ///  2xx
        /// </summary>
        Thunder,

        /// <summary>
        ///  3xx
        /// </summary>
        Drizzle,

        /// <summary>
        ///  5xx
        /// </summary>
        Rain,

        /// <summary>
        ///  <para>7xx</para>
        ///  <para>Mist, smoke, haze, sand/dust whirls, fog sand, dust volcanic ash, squalls, tornado.</para>
        /// </summary>
        Atmosphere,

        /// <summary>
        ///  8xx
        /// </summary>
        Clear,

        /// <summary>
        ///  80x
        /// </summary>
        Clouds,
    }

    public interface IOpenWeathermapService
    {
        Task<string> GetForecast();
        WeatherDescription GetForecastDescription(int weatherValue);
    }

    public class OpenWeathermapService : Service<OpenWeathermapSettings>, IOpenWeathermapService
    {
        private readonly IInfluxWriterService _InfluxWrite;
        public OpenWeathermapService(ILogger<OpenWeathermapService> logger, IOptions<OpenWeathermapSettings> settings, IInfluxWriterService influxWrite) : base(logger, settings)
        {
            _InfluxWrite = influxWrite;
        }

        public override bool ValidateSettings()
        {
            if (Settings.Latitude < -90
                || Settings.Latitude > 90
                || Settings.Longitude < -180
                || Settings.Longitude > 180
                )
            {
                return false;
            }
            return true;
        }

        public async Task<string> GetForecast()
        {
            HttpClient client = new HttpClient();
            HttpResponseMessage r = await client.GetAsync($"https://api.openweathermap.org/data/3.0/onecall?lat={Settings.Latitude}&lon={Settings.Longitude}&exclude=current,minutely,hourly,alerts&appid={Settings.ApiKey}&units=metric");
            r.EnsureSuccessStatusCode();
            return await r.Content.ReadAsStringAsync();
        }

        public WeatherDescription GetForecastDescription(int weatherValue)
        {
            if(weatherValue < 300)
            {
                throw new NotImplementedException();
            }
            else if(weatherValue < 400)
            {
                // 3xx
                return WeatherDescription.Thunder;
            }
            else if (weatherValue < 500)
            {
                return WeatherDescription.Drizzle;
            }
            else if (weatherValue < 500)
            {
                throw new NotImplementedException();
            }
            else if (weatherValue < 600)
            {
                return WeatherDescription.Rain;
            }
            else if (weatherValue < 800)
            {
                return WeatherDescription.Atmosphere;
            }
            else if (weatherValue == 800)
            {
                return WeatherDescription.Clear;
            }
            else if (weatherValue < 910)
            {
                return WeatherDescription.Clouds;
            }
            else
            {
                throw new NotImplementedException();
            }
        }
    }
}
