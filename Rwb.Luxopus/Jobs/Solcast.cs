using Rwb.Luxopus.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    public class Solcast : Job
    {
        private const string Measurement = "daily";

        private readonly ISolcastService _Solcast;
        private readonly IInfluxWriterService _InfluxWrite;

        public Solcast(ILogger<LuxMonitor> logger, ISolcastService solcast, IInfluxWriterService influx)  :base(logger)
        {
            _Solcast = solcast;
            _InfluxWrite = influx;
        }

        public override async Task RunAsync(CancellationToken cancellationToken)
        {
            string json = await _Solcast.GetForecasts();
            using (JsonDocument j = JsonDocument.Parse(json))
            {
                LineDataBuilder lines = new LineDataBuilder();
                foreach (var z in j.RootElement.GetArray("forecasts"))
                {
                    JsonElement.ObjectEnumerator p = z.EnumerateObject();
                    DateTime d = p.Single(z => z.Name == "period_end").GetDate().Value.ToUniversalTime();
                    decimal e = p.Single(z => z.Name == "pv_estimate").Value.GetDecimal();
                    lines.Add("solcast", "actual", e, d);
                }
                await _InfluxWrite.WriteAsync(lines);
            }

            json = await _Solcast.GetEstimatedActuals();
            using (JsonDocument j = JsonDocument.Parse(json))
            {
                LineDataBuilder lines = new LineDataBuilder();
                foreach (var z in j.RootElement.GetArray("estimated_actuals"))
                {
                    JsonElement.ObjectEnumerator p = z.EnumerateObject();
                    DateTime d = p.Single(z => z.Name == "period_end").GetDate().Value.ToUniversalTime();
                    decimal e = p.Single(z => z.Name == "pv_estimate").Value.GetDecimal();
                    lines.Add("solcast", "actual", e, d);
                }
                await _InfluxWrite.WriteAsync(lines);
            }
        }
    }
}
