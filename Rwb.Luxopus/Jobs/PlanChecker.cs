using Rwb.Luxopus.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Rwb.Luxopus.Jobs
{

    /// <summary>
    /// <para>
    /// Check that plans are running. Could
    /// </para>
    /// </summary>
    public class PlanChecker : Job
    {
        private readonly ILuxopusPlanService _Plans;
        private readonly ILuxService _Lux;
        private readonly IInfluxQueryService _InfluxQuery;

        public PlanChecker(ILogger<LuxMonitor> logger, ILuxopusPlanService plans, ILuxService lux, IInfluxQueryService influxQuery)  :base(logger)
        {
            _Plans = plans;
            _Lux= lux;
            _InfluxQuery = influxQuery;
        }

        public override async Task RunAsync(CancellationToken cancellationToken)
        {
            if( DateTime.UtcNow.Minute % 30 > 2) { return; }

            DateTime t0 = new DateTime(2023, 03, 31, 18, 00, 00);
            IEnumerable<Plan> ps = _Plans.LoadAll(t0.AddHours(3));

            Plan? plan = _Plans.Load(DateTime.UtcNow);
            if(plan == null)
            {
                Logger.LogError($"No current plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
                return;
            }

            HalfHourPlan p = plan.Current;

            if(p.Action == null)
            {
                await _Lux.ResetAsync();
                return;
            }

            // Check that it's doing what it's supposed to be doing.
            // update settings and log warning in case of discrepancy.

            // Are we on target?
            // If not then what can we do about it?

            Dictionary<string, string> settings = await _Lux.GetSettingsAsync();

            bool defaultCase = true;
            if(p.Action.DischargeToGrid < 100)
            {
                defaultCase = false;
                await _Lux.SetChargeFromGridAsync(p.Start, p.Start.AddMinutes(30), p.Action.DischargeToGrid);
            }

            if ( p.Action.ChargeFromGrid > 0)
            {
                defaultCase = false;
                await _Lux.SetChargeFromGridAsync(p.Start, p.Start.AddMinutes(30), p.Action.ChargeFromGrid);
            }

            if ( p.Action.ExportGeneration)
            {
                defaultCase = false;
                await _Lux.SetBatteryChargeRate(1);
            }

            if (defaultCase)
            {
                await _Lux.ResetAsync();
            }
        }
    }
}
