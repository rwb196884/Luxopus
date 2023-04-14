using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

    public interface IOpenWeathermapService
    {
        Task<(int, int)> GetForecasts();
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

        public async Task<(int, int)> GetForecasts()
        {
            HttpClient client = new HttpClient();
            HttpResponseMessage r = await client.GetAsync($"https://api.openweathermap.org/data/3.0/onecall?lat={Settings.Latitude}&lon={Settings.Longitude}&exclude=current,minutely,hourly,alerts&appid={Settings.ApiKey}&units=metric");
            // TODO: parse the response and extract cloud and UVI. Day length now comes from sun service.
            return (0, 0);
        }
    }
}
