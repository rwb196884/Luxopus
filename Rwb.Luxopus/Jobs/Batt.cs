using Rwb.Luxopus.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    public class Batt : Job
    {
        private readonly ILuxService _Lux;
        private readonly IInfluxQueryService _Influx;

        public Batt(ILogger<LuxMonitor> logger, ILuxService lux, IInfluxQueryService influx)  :base(logger)
        {
            _Lux= lux;
            _Influx= influx;
        }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            (DateTime tBattLevel, int battLevel) = (await _Influx.QueryAsync(@$"
from(bucket: ""{_Influx.Bucket}"")
  |> range(start: -30m, stop: now())
  |> filter(fn: (r) => (r[""_measurement""] == ""battery"" and r[""_field""] == ""level"") or (r[""_measurement""] == ""inverter"" and r[""_field""] == ""batt_level""))
  |> last()
"))
        .First()
        .GetValues<int>()
        .Single();

            if(battLevel > 97) { 
                // TO TO: Get current charge rate.
                await _Lux.SetBatteryChargeRate(5);
            }
            else if( battLevel < 30)
            {
                await _Lux.SetBatteryChargeRate(90);
                await _Lux.SetDishargeToGridAsync(DateTime.Now, DateTime.Now, 101); // Percent out of range disables discharge.
            }
        }
    }
}
