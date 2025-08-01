﻿using Rwb.Luxopus.Services;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    public class LuxMonitor : Job
    {
        private const string Measurement = "inverter";

        private readonly ILuxService _Lux;
        private readonly IInfluxWriterService _Influx;

        public LuxMonitor(ILogger<LuxMonitor> logger, ILuxService lux, IInfluxWriterService influx) : base(logger)
        {
            _Lux = lux;
            _Influx = influx;
        }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            LineDataBuilder lines = new LineDataBuilder();

            (double today, double tomorrow) = await _Lux.Forecast();
            lines.Add("forecast", "today", today);
            lines.Add("forecast", "tomorrow", tomorrow);

            string json = await _Lux.GetInverterRuntimeAsync();
            using (JsonDocument j = JsonDocument.Parse(json))
            {
                JsonElement.ObjectEnumerator r = j.RootElement.EnumerateObject();

                JsonProperty st = r.SingleOrDefault(z => z.Name == "statusText");
                if ( st.Value.ValueKind != JsonValueKind.Undefined && st.Value.GetString()  == "offline")
                {
                    return;
                }
                //DateTime t = r.Single(z => z.Name == "serverTime").GetDate().Value;

                lines.Add(Measurement, "generation", r.Single(z => z.Name == "ppv").Value.GetInt32());
                lines.Add(Measurement, "inverter_output", r.Single(z => z.Name == "pinv").Value.GetInt32());
                lines.Add(Measurement, "batt_charge", r.Single(z => z.Name == "pCharge").Value.GetInt32());
                lines.Add(Measurement, "batt_discharge", r.Single(z => z.Name == "pDisCharge").Value.GetInt32());
                lines.Add(Measurement, "export", r.Single(z => z.Name == "pToGrid").Value.GetInt32());
                lines.Add(Measurement, "import", r.Single(z => z.Name == "pToUser").Value.GetInt32());
                lines.Add(Measurement, "batt_level", r.Single(z => z.Name == "soc").Value.GetInt32());
                lines.Add(Measurement, "inverter_input", r.Single(z => z.Name == "prec").Value.GetInt32());
                lines.Add(Measurement, "consumption", r.Single(z => z.Name == "consumptionPower").Value.GetInt32());
                lines.Add(Measurement, "vpv1", r.Single(z => z.Name == "vpv1").Value.GetInt32());
                lines.Add(Measurement, "vpv2", r.Single(z => z.Name == "vpv2").Value.GetInt32());
                //lines.Add("battery", "level", r.Single(z => z.Name == "soc").Value.GetInt32()); // Old version.
            }
            await _Influx.WriteAsync(lines);
        }
    }
}
