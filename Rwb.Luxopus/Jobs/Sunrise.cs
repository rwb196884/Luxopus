using InfluxDB.Client.Core.Flux.Domain;
using Microsoft.Extensions.Logging;
using NodaTime;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    public class Sunrise : Job
    {
        private readonly ISunService _Sun;
        private readonly IInfluxQueryService _InfluxQuery;
        private readonly IInfluxWriterService _InfluxWrite;

        public Sunrise(ILogger<LuxMonitor> logger, ISunService sun, IInfluxQueryService influxQuery, IInfluxWriterService influxWrite)  :base(logger)
        {
            _Sun = sun;
            _InfluxQuery = influxQuery;
            _InfluxWrite = influxWrite;
        }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            DateTime t = await GetLatestSunrise();
            LineDataBuilder lines = new LineDataBuilder();
            while( t.Date < DateTime.UtcNow.Date.AddDays(2))
            {
                DateTime? s = null;
                s = _Sun.GetSunrise(t);
                if (s.HasValue)
                {
                    lines.Add("sun", "risen", 1, s.Value);
                }
                else
                {
                    Logger.LogWarning($"No sunrise for {t.ToString("dd MMM yy")}");
                }

                s = _Sun.GetSunset(t);
                if (s.HasValue)
                {
                    lines.Add("sun", "risen", 0, s.Value);
                }
                else
                {
                    Logger.LogWarning($"No sunset for {t.ToString("dd MMM yy")}");
                }

                t = t.AddDays(1);
            }
            await _InfluxWrite.WriteAsync(lines);
        }

        private async Task<DateTime> GetLatestSunrise()
        {
            string flux = $@"
from(bucket:""{_InfluxQuery.Bucket}"")
  |> range(start: -1y, stop: 2d)
  |> filter(fn: (r) => r[""_measurement""] == ""sun"" and r[""_field""] == ""risen"")
  |> last()
";
            List<FluxTable> q = await _InfluxQuery.QueryAsync(flux);
            if (q.Count > 0 && q[0].Records.Count > 0)
            {
                // There is a value.
                object o = q[0].Records[0].Values["_time"];
                if (o.GetType() == typeof(Instant))
                {
                    return ((Instant)o).ToDateTimeUtc();
                }
                return (DateTime)o;
            }

            return await GetEarliestInverterData();
        }
        private async Task<DateTime> GetEarliestInverterData()
        {
            string flux = $@"
from(bucket:""{_InfluxQuery.Bucket}"")
  |> range(start: -1y, stop: 2d)
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"")
  |> group(columns: [""_measurement""])
  |> first()
";
            List<FluxTable> q = await _InfluxQuery.QueryAsync(flux);
            if (q.Count > 0 && q[0].Records.Count > 0)
            {
                // There is a value.
                object o = q[0].Records[0].Values["_time"];
                if (o.GetType() == typeof(Instant))
                {
                    return ((Instant)o).ToDateTimeUtc();
                }
                return (DateTime)o;
            }
            else
            {

            }
            return DateTime.Now.AddYears(-1);
        }
    }
}
