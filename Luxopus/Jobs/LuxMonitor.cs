using Luxopus.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Luxopus.Jobs
{
    internal class LuxMonitor : Job
    {
        private const string Measurement = "inverter";

        private readonly ILuxService _Lux;
        private readonly IInfluxWriterService _Influx;

        public LuxMonitor(ILogger<LuxMonitor> logger, ILuxService lux, IInfluxWriterService influx)  :base(logger)
        {
            _Lux= lux;
            _Influx= influx;
        }

        public override async Task RunAsync(CancellationToken cancellationToken)
        {
            LineDataBuilder lines = new LineDataBuilder();
            string json = await _Lux.GetInverterRuntimeAsync();
            using (JsonDocument j = JsonDocument.Parse(json))
            {
                JsonElement.ObjectEnumerator r = j.RootElement.EnumerateObject();

                lines.Add(Measurement, "generation", r.Single(z => z.Name == "ppv").Value.GetInt32());
                lines.Add(Measurement, "inverter_output", r.Single(z => z.Name == "pinv").Value.GetInt32());
                lines.Add(Measurement, "batt_charge", r.Single(z => z.Name == "pCharge").Value.GetInt32());
                lines.Add(Measurement, "batt_discharge", r.Single(z => z.Name == "pDisCharge").Value.GetInt32());
                lines.Add(Measurement, "export", r.Single(z => z.Name == "pToGrid").Value.GetInt32());
                lines.Add(Measurement, "import", r.Single(z => z.Name == "pToUser").Value.GetInt32());
                lines.Add(Measurement, "level", r.Single(z => z.Name == "soc").Value.GetInt32());

                //foreach (JsonProperty p in r)
                //{
                //    switch (p.Value.ValueKind)
                //    {
                //        case JsonValueKind.String:
                //            lines.Add(Measurement, p.Name, "\"" + p.Value.GetString() + "\"");
                //            break;
                //        case JsonValueKind.Number:
                //            lines.Add(Measurement, p.Name, p.Value.GetInt32());
                //            break;
                //        case JsonValueKind.True:
                //        case JsonValueKind.False:
                //            lines.Add(Measurement, p.Name, p.Value.GetBoolean());
                //            break;
                //        default:
                //            throw new NotImplementedException($"JsonValueKind {p.Value.ValueKind}");
                //    }
                //}
            }
            await _Influx.WriteAsync(lines);
        }
    }
}
