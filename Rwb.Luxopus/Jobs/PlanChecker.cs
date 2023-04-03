using Rwb.Luxopus.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;

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
        private readonly IEmailService _Email;

        public PlanChecker(ILogger<LuxMonitor> logger, ILuxopusPlanService plans, ILuxService lux, IInfluxQueryService influxQuery, IEmailService email) : base(logger)
        {
            _Plans = plans;
            _Lux = lux;
            _InfluxQuery = influxQuery;
            _Email = email;
        }

        public override async Task RunAsync(CancellationToken cancellationToken)
        {
            //DateTime t0 = new DateTime(2023, 03, 31, 18, 00, 00);
            DateTime t0 = DateTime.UtcNow;
            IEnumerable<Plan> ps = _Plans.LoadAll(t0);

            Plan? plan = _Plans.Load(DateTime.UtcNow);
            if(plan == null)
            {
                Logger.LogError($"No current plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
                return;
            }

            StringBuilder actions = new StringBuilder();

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
            (bool inEnabled, DateTime inStart, DateTime inStop, int inBatteryLimitPercent) = _Lux.GetChargeFromGrid(settings);
            (bool outEnabled, DateTime outStart, DateTime outStop, int outBatteryLimitPercent)  = _Lux.GetDishargeToGrid(settings);
            int battChargeRate = _Lux.GetBatteryChargeRate(settings);

            bool defaultCase = true;
            if(p.Action.DischargeToGrid < 100 && (!outEnabled || outStart > p.Start || outStop < p.Start.AddMinutes(30) || outBatteryLimitPercent > p.Action.DischargeToGrid) )
            {
                defaultCase = false;
                //await _Lux.SetDishargeToGridAsync(p.Start, p.Start.AddMinutes(30), p.Action.DischargeToGrid);
                actions.AppendLine($"SetDishargeToGridAsync({p.Start.ToString("HH:mm")}, {p.Start.AddMinutes(30).ToString("HH:mm")} {p.Action.DischargeToGrid}) was {outEnabled} {outStart.ToString("HH:mm")} {outStop.ToString("HH:mm")} {outBatteryLimitPercent}%");
            }

            if ( p.Action.ChargeFromGrid > 0 && (!inEnabled || inStart > p.Start || inStop < p.Start.AddMinutes(30) || inBatteryLimitPercent < p.Action.ChargeFromGrid))
            {
                defaultCase = false;
                //await _Lux.SetChargeFromGridAsync(p.Start, p.Start.AddMinutes(30), p.Action.ChargeFromGrid);
                actions.AppendLine($"SetChargeFromGridAsync({p.Start.ToString("HH:mm")}, {p.Start.AddMinutes(30).ToString("HH:mm")} {p.Action.DischargeToGrid} was {inEnabled} {inStart.ToString("HH:mm")} {inStop.ToString("HH:mm")} {inBatteryLimitPercent}%");
            }

            if ( p.Action.ExportGeneration && battChargeRate > 5)
            {
                defaultCase = false;
                //await _Lux.SetBatteryChargeRate(1);
                actions.AppendLine($"SetBatteryChargeRate(1) was {battChargeRate}");
            }

            if (defaultCase)
            {
                await _Lux.ResetAsync();
                actions.AppendLine($"ResetAsync");
            }

            string message = actions.ToString();
            if(!string.IsNullOrEmpty(message))
            {
                _Email.SendEmail($"PlanChecker {DateTime.Now.ToString("dd MMM HH:mm")}", message);
            }
        }
    }
}
