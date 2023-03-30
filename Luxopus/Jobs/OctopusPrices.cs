using InfluxDB.Client.Core.Flux.Domain;
using Luxopus.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Luxopus.Jobs
{
    internal class OctopusPrices : Job
    {
        private const string Measurement = "prices";

        private readonly IOctopusService _Octopus;
        private readonly IInfluxQueryService _InfluxQuery;
        private readonly IInfluxWriterService _InfluxWrite;

        public OctopusPrices(ILogger<LuxMonitor> logger, IOctopusService octopus, IInfluxQueryService influxQuery, IInfluxWriterService influxWrite)  :base(logger)
        {
            _Octopus = octopus;
            _InfluxQuery = influxQuery;
            _InfluxWrite = influxWrite;
        }

        private static string GetProductOfTariff(string tariffCode)
        {
            // Remove E-1R- from the start.
            // Remove -E from the end.
            // https://forum.octopus.energy/t/product-codes-tariff-codes/5154
            string a = tariffCode.Substring(4);
            return a.Substring(0, a.Length - 2);
        }

        private const string Tariffs = "E-1R-AGILE-FLEX-22-11-25-E,E-1R-AGILE-OUTGOING-19-05-13-E";

        public override async Task RunAsync(CancellationToken cancellationToken)
        {
            foreach(string t in ( await _Octopus.GetElectricityTariffs()).Where(z => !z.ValidTo.HasValue || z.ValidTo > DateTime.Now.AddDays(-5)).Select(z => z.Code).Union(Tariffs.Split(',')).Distinct() )
            {
                Dictionary<string, string> tags = new Dictionary<string, string>()
                {
                        { "fuel", "electricity" },
                        { "tariff", t },
                        { "type", t.Contains("OUTGOING") ? "sell" : "buy" }
                };

                string p = GetProductOfTariff(t);
                DateTime from = await GetLatestPriceAsync(t);
                IEnumerable<Price> prices = await _Octopus.GetElectricityPrices(p, t, from, DateTime.Now.Date.AddDays(1).AddHours(22));
                LineDataBuilder lines = new LineDataBuilder();
                foreach (Price price in prices)
                {
                    lines.Add(Measurement, tags, "prices", price.Pence, price.ValidFrom);
                }
                await _InfluxWrite.WriteAsync(lines);
            }
        }

        private async Task<DateTime> GetLatestPriceAsync(string tariffCode)
        {
            string bucket = "solar";
            string flux = $@"
from(bucket:""{bucket}"")
  |> range(start: -1y, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""prices"" and r[""tariff""] == ""{tariffCode}"")
  |> last()
";
            List<FluxTable> q = await _InfluxQuery.QueryAsync(flux);
            if (q.Count > 0 && q[0].Records.Count > 0)
            {
                object o = q[0].Records[0].Values["_time"];
                return (DateTime)o;
            }
            return DateTime.Now.AddYears(-1);
        }
    }
}
