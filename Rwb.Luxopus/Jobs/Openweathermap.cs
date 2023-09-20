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
                foreach (JsonElement jDay in j.RootElement.GetArray("daily"))
                {
                    JsonElement.ObjectEnumerator day = jDay.EnumerateObject();
                    int s = day.First(z => z.Name == "dt").Value.GetInt32();
                    DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
                    DateTime t = epoch.AddSeconds(s);

                    int clouds = day.First(z => z.Name == "clouds").Value.GetInt32();
                    lines.Add("weather", "cloud", Math.Round(Convert.ToDecimal(clouds)), t);

                    double uvi = day.First(z => z.Name == "uvi").Value.GetDouble();
                    lines.Add("weather", "uvi", uvi, t);

                    int sunrise = day.First(z => z.Name == "sunrise").Value.GetInt32();
                    int sunset = day.First(z => z.Name == "sunset").Value.GetInt32();
                    lines.Add("weather", "daylen", Math.Round(Convert.ToDecimal(sunset - sunrise) / 3600M, 2), t);
                }
            }
            await _InfluxWrite.WriteAsync(lines);
        }
    }
}
