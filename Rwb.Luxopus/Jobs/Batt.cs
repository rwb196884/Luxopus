using Rwb.Luxopus.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

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
            int battLevel = await _Influx.GetBatteryLevelAsync();

            Dictionary<string, string> settings = await _Lux.GetSettingsAsync();
            int battChargeRate = _Lux.GetBatteryChargeRate(settings);

            if(battLevel > 97) {
                if (battChargeRate > 5)
                {
                    Logger.LogWarning($"Changing battery charge rate from {battChargeRate} to 5 becuase battery level is {battLevel}.");
                    await _Lux.SetBatteryChargeRate(5);
                }
            }
            else if( battLevel < 25)
            {
                if (battChargeRate < 95)
                {
                    Logger.LogWarning($"Changing battery charge rate from {battChargeRate} to 96 becuase battery level is {battLevel}.");
                    await _Lux.SetBatteryChargeRate(96);
                }

                (bool enabled, DateTime start, DateTime stop, int batteryLimitPercent) = _Lux.GetDishargeToGrid(settings);
                if (enabled)
                {
                    Logger.LogWarning($"Changing force discharge fom enabled to disabled becuase battery level is {battLevel}.");
                    await _Lux.SetDishargeToGridAsync(DateTime.Now, DateTime.Now, 101); // Percent out of range disables discharge.
                }
            }
        }
    }
}
