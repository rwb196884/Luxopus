using Rwb.Luxopus.Services;

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
                DateTime? tr = _Sun.GetSunrise(t);
                if (tr.HasValue)
                {
                    lines.Add("sun", "risen", 1, tr.Value);
                }
                else
                {
                    Logger.LogWarning($"No sunrise for {t.ToString("dd MMM yy")}");
                }

                DateTime? ts = _Sun.GetSunset(t);
                if (ts.HasValue)
                {
                    lines.Add("sun", "risen", 0, ts.Value);
                }
                else
                {
                    Logger.LogWarning($"No sunset for {t.ToString("dd MMM yy")}");
                }

                if(tr.HasValue && ts.HasValue)
                {
                    lines.Add("sun", "daylen", Convert.ToInt32(Math.Floor((ts.Value - tr.Value).TotalSeconds)), ts.Value.Date);
                }

                t = t.AddDays(1);
            }
            await _InfluxWrite.WriteAsync(lines);
        }

        private async Task<DateTime> GetLatestSunrise()
        {
            string flux = $@"
from(bucket:""{_InfluxQuery.Bucket}"")
  |> range(start: -1w, stop: 2d)
  |> filter(fn: (r) => r[""_measurement""] == ""sun"" and r[""_field""] == ""risen"" and r[""_value""] == 1)
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
