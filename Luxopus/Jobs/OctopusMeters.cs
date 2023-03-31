using InfluxDB.Client.Core.Flux.Domain;
using Luxopus.Services;
using Microsoft.Extensions.Logging;
using NodaTime;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Luxopus.Jobs
{
    internal class OctopusMeters : Job
    {
        private const string Measurement = "meters";

        private readonly IOctopusService _Octopus;
        private readonly IInfluxQueryService _InfluxQuery;
        private readonly IInfluxWriterService _InfluxWrite;

        public OctopusMeters(ILogger<LuxMonitor> logger, IOctopusService octopusService, IInfluxQueryService influxQuery, IInfluxWriterService influxWrite) : base(logger)
        {
            _Octopus = octopusService;
            _InfluxQuery= influxQuery;
            _InfluxWrite = influxWrite;
        }

        public override async Task RunAsync(CancellationToken cancellationToken)
        {
            foreach (string mpan in await _Octopus.GetElectricityMeterPoints())
            {
                foreach (string serialNumber in await _Octopus.GetElectricityMeters(mpan))
                {
                    DateTime from = await GetLatestMeterReadingAsync(serialNumber);
                    Dictionary<string, string> tags = new Dictionary<string, string>()
                    {
                        { "fuel", "electricity" },
                        { "mpan", mpan },
                        { "serialNumber", serialNumber},
                    };

                    IEnumerable<MeterReading> m = await _Octopus.GetElectricityMeterReadings(mpan, serialNumber, from, DateTime.Now);
                    LineDataBuilder lines = new LineDataBuilder();
                    foreach (MeterReading mr in m)
                    {
                        lines.Add(Measurement, tags, "consumption", mr.Consumption, mr.IntervalStart);
                    }
                    await _InfluxWrite.WriteAsync(lines);
                }
            }

            foreach (string mprn in await _Octopus.GetGasMeterPoints())
            {
                foreach (string serialNumber in await _Octopus.GetGasMeters(mprn))
                {
                    DateTime from = await GetLatestMeterReadingAsync(serialNumber);
                    Dictionary<string, string> tags = new Dictionary<string, string>()
                    {
                        { "fuel", "gas" },
                        {"mprn", mprn },
                        { "serialNumber", serialNumber}
                    };

                    IEnumerable<MeterReading> m = await _Octopus.GetGasMeterReadings(mprn, serialNumber, from, DateTime.Now);
                    LineDataBuilder lines = new LineDataBuilder();
                    foreach (MeterReading mr in m)
                    {
                        lines.Add(Measurement, tags, "consumption", mr.Consumption, mr.IntervalStart);
                    }
                    await _InfluxWrite.WriteAsync(lines);
                }
            }
        }

        private async Task<DateTime> GetLatestMeterReadingAsync(string serialNumber)
        {
            string flux = $@"
from(bucket:""{_InfluxQuery.Bucket}"")
  |> range(start: -1y, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""meters"" and r[""serialNumber""] == ""{serialNumber}"")
  |> last()
";
            List<FluxTable> q = await _InfluxQuery.QueryAsync(flux);
            if (q.Count > 0 && q[0].Records.Count > 0)
            {
                object o = q[0].Records[0].Values["_time"];
                if( o.GetType() == typeof(Instant))
                {
                    return ((Instant)o).ToDateTimeUtc();
                }
                return (DateTime)o;
            }
            return DateTime.Now.AddYears(-1);
        }
    }
}
