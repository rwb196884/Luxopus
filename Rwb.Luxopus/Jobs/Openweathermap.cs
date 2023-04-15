using Rwb.Luxopus.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    public class Openweathermap : Job
    {
        private readonly IOpenWeathermapService _Weather;
        private readonly IInfluxWriterService _InfluxWrite;

        public Openweathermap(ILogger<LuxMonitor> logger, IOpenWeathermapService weather, IInfluxWriterService influx) : base(logger)
        {
            _Weather = weather;
            _InfluxWrite = influx;
        }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            LineDataBuilder lines = new LineDataBuilder();
            string json = await _Weather.GetForecast();
            using (JsonDocument j = JsonDocument.Parse(json))
            {
                JsonElement.ObjectEnumerator day = j.RootElement.GetArray("daily").First().EnumerateObject();

                int clouds = day.First(z => z.Name == "clouds").Value.GetInt32();
                lines.Add("weather", "cloud", clouds);

                double uvi = day.First(z => z.Name == "uvi").Value.GetDouble();
                lines.Add("weather", "uvi", uvi);

                int sunrise = day.First(z => z.Name == "sunrise").Value.GetInt32();
                int sunset = day.First(z => z.Name == "sunset").Value.GetInt32();
                lines.Add("weather", "daylen", Math.Round(Convert.ToDecimal(sunset - sunrise) / 3600M, 2));
            }
            await _InfluxWrite.WriteAsync(lines);
        }
    }
}
