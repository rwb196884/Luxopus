using Rwb.Luxopus.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    /// <summary>
    /// This needs to run at 11pm at inverter time.
    /// </summary>
    public class LuxDaily : Job
    {
        private const string Measurement = "daily";

        private readonly ILuxService _Lux;
        private readonly IInfluxWriterService _Influx;
        private readonly IEmailService _Email;

        public LuxDaily(ILogger<LuxMonitor> logger, ILuxService lux, IInfluxWriterService influx, IEmailService email)  :base(logger)
        {
            _Lux= lux;
            _Influx= influx;
            _Email = email;
        }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            DateTime t0 = DateTime.UtcNow;
            DateTime tLux = _Lux.ToLocal(t0);
            if( tLux.Hour != 23)
            {
                Logger.LogInformation($"LuxDaily: local hour is {tLux.Hour}.");
                return;
            }
            Logger.LogInformation($"LuxDaily: local hour is {tLux.Hour}.");

            LineDataBuilder lines = new LineDataBuilder();
            string json = await _Lux.GetInverterEnergyInfoAsync();
            using (JsonDocument j = JsonDocument.Parse(json))
            {
                JsonElement.ObjectEnumerator r = j.RootElement.EnumerateObject();

                lines.Add(Measurement, "generation", r.Single(z => z.Name == "todayYielding").Value.GetInt32());
                lines.Add(Measurement, "export", r.Single(z => z.Name == "todayExport").Value.GetInt32());
                lines.Add(Measurement, "import", r.Single(z => z.Name == "todayImport").Value.GetInt32());
                lines.Add(Measurement, "consumption", r.Single(z => z.Name == "todayUsage").Value.GetInt32());
                lines.Add(Measurement, "to_batt", r.Single(z => z.Name == "todayCharging").Value.GetInt32());
                lines.Add(Measurement, "from_batt", r.Single(z => z.Name == "todayDischarging").Value.GetInt32());
            }
            _Email.SendEmail("LUX daily", string.Join(Environment.NewLine, lines.GetLineData()));
            await _Influx.WriteAsync(lines);
        }
    }
}
