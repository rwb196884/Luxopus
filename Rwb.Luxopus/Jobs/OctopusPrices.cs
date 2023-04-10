using InfluxDB.Client.Core.Flux.Domain;
using Microsoft.Extensions.Logging;
using NodaTime;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    public class OctopusPrices : Job
    {
        private const string Measurement = "prices";

        private readonly IOctopusService _Octopus;
        private readonly IInfluxQueryService _InfluxQuery;
        private readonly IInfluxWriterService _InfluxWrite;
        private readonly IEmailService _Email;
        private readonly ISmsService _Sms;

        public OctopusPrices(ILogger<LuxMonitor> logger, IOctopusService octopus, IInfluxQueryService influxQuery, IInfluxWriterService influxWrite, IEmailService email, ISmsService sms)  :base(logger)
        {
            _Octopus = octopus;
            _InfluxQuery = influxQuery;
            _InfluxWrite = influxWrite;
            _Email = email;
            _Sms = sms;
        }

        private static string GetProductOfTariff(string tariffCode)
        {
            // Remove E-1R- from the start.
            // Remove -E from the end.
            // https://forum.octopus.energy/t/product-codes-tariff-codes/5154
            string a = tariffCode.Substring(5);
            return a.Substring(0, a.Length - 2);
        }

        private const string ElectricityTariffs = "E-1R-AGILE-FLEX-22-11-25-E,E-1R-AGILE-OUTGOING-19-05-13-E,E-1R-FLUX-EXPORT-23-02-14-E,E-1R-FLUX-IMPORT-23-02-14-E";

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            Dictionary<string, List<Price>> negativePrices = new Dictionary<string, List<Price>>();

            foreach (string t in ( await _Octopus.GetElectricityTariffs()).Where(z => !z.ValidTo.HasValue || z.ValidTo > DateTime.Now.AddDays(-5)).Select(z => z.Code).Union(ElectricityTariffs.Split(',')).Distinct() )
            {
                Dictionary<string, string> tags = new Dictionary<string, string>()
                {
                        { "fuel", "electricity" },
                        { "tariff", t },
                        { "type", t.Contains("OUTGOING") || t.Contains("EXPORT") ? "sell" : "buy" }
                };

                string p = GetProductOfTariff(t);
                DateTime from = (await GetLatestPriceAsync(t)).AddMinutes(15);
                DateTime to = DateTime.Now.Date.AddDays(1).AddHours(22);
                IEnumerable<Price> prices = await _Octopus.GetElectricityPrices(p, t, from, to);
                LineDataBuilder lines = new LineDataBuilder();
                foreach (Price price in prices)
                {
                    lines.Add(Measurement, tags, "prices", price.Pence, price.ValidFrom);
                }
                Logger.LogInformation($"Got {prices.Count()} prices for tariff {t} from {from.ToString("dd MMM HH:mm")} to {to.ToString("dd MMM HH:mm")}.");
                await _InfluxWrite.WriteAsync(lines);
                if(prices.Any(z => z.Pence < 0))
                {
                    negativePrices.Add(t, prices.Where(z => z.Pence < 0).ToList());
                }
            }

            if (negativePrices.Any())
            {
                StringBuilder email = new StringBuilder();
                foreach(string t in negativePrices.Keys)
                {
                    email.AppendLine(t);
                    foreach(Price p in negativePrices[t].OrderBy(z => z.ValidFrom))
                    {
                        email.AppendLine($"{p.ValidFrom:HH:mm zzz} {p.Pence:0.00}");
                    }
                    email.AppendLine();
                }

                _Email.SendEmail("Negative electricity prices", email.ToString());
                _Sms.SendSms($"Negative electicity prices!");
            }

            foreach (string t in (await _Octopus.GetGasTariffs()).Where(z => !z.ValidTo.HasValue || z.ValidTo > DateTime.Now.AddDays(-5)).Select(z => z.Code).Distinct())
            {
                Dictionary<string, string> tags = new Dictionary<string, string>()
                {
                        { "fuel", "gas" },
                        { "tariff", t },
                        { "type", t.Contains("OUTGOING") || t.Contains("EXPORT") ? "sell" : "buy" }
                };

                string p = GetProductOfTariff(t);
                DateTime from = (await GetLatestPriceAsync(t)).AddMinutes(15);
                DateTime to = DateTime.Now.Date.AddDays(1).AddHours(22);
                IEnumerable<Price> prices = await _Octopus.GetGasPrices(p, t, from, to);
                LineDataBuilder lines = new LineDataBuilder();
                foreach (Price price in prices)
                {
                    lines.Add(Measurement, tags, "prices", price.Pence, price.ValidFrom);
                }
                Logger.LogInformation($"Got {prices.Count()} prices for tariff {t} from {from.ToString("dd MMM HH:mm")} to {to.ToString("dd MMM HH:mm")}.");
                await _InfluxWrite.WriteAsync(lines);
            }
        }

        private async Task<DateTime> GetLatestPriceAsync(string tariffCode)
        {
            string flux = $@"
from(bucket:""{_InfluxQuery.Bucket}"")
  |> range(start: -1y, stop: 2d)
  |> filter(fn: (r) => r[""_measurement""] == ""prices"" and r[""tariff""] == ""{tariffCode}"")
  |> last()
";
            List<FluxTable> q = await _InfluxQuery.QueryAsync(flux);
            if (q.Count > 0 && q[0].Records.Count > 0)
            {
                object o = q[0].Records[0].Values["_time"];
                if (o.GetType() == typeof(Instant))
                {
                    return ((Instant)o).ToDateTimeUtc();
                }
                return (DateTime)o;
            }
            return DateTime.Now.AddYears(-1);
        }
    }
}
