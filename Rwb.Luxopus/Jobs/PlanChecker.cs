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
    /// Check that plans are running. Simple version: look only at the current period.
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

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            //DateTime t0 = new DateTime(2023, 03, 31, 18, 00, 00);
            DateTime t0 = DateTime.UtcNow;
            IEnumerable<Plan> ps = _Plans.LoadAll(t0);

            Plan? plan = _Plans.Load(DateTime.UtcNow);
            if (plan == null)
            {
                Logger.LogError($"No plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
                // If there is plan then default configuration will be set.
            }

            HalfHourPlan? p = plan?.Current;

            if(p == null)
            {
                Logger.LogError($"No current plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
            }
            if (plan?.Next == null)
            {
                Logger.LogError($"No next plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
            }

            DateTime tStart = p?.Start ?? DateTime.UtcNow;
            DateTime tNext = plan?.Next?.Start ?? tStart.AddMinutes(30);

            StringBuilder actions = new StringBuilder();


            // Check that it's doing what it's supposed to be doing.
            // update settings and log warning in case of discrepancy.

            // Are we on target?
            // If not then what can we do about it?

            Dictionary<string, string> settings = await _Lux.GetSettingsAsync();
            int battChargeRate = _Lux.GetBatteryChargeRate(settings);
            int battDischargeRate = _Lux.GetBatteryDischargeRate(settings);

            if(battDischargeRate != (p?.Action?.BatteryDischargeRate ?? 100))
            {
                await _Lux.SetBatteryDishargeRateAsync(p.Action?.BatteryDischargeRate ?? 100);
                actions.AppendLine($"SetBatteryDishargeRate({p.Action?.BatteryDischargeRate ?? 100}) was {battDischargeRate}.");
            }

            // Discharge to grid.
            (bool outEnabled, DateTime outStart, DateTime outStop, int outBatteryLimitPercent) = _Lux.GetDishargeToGrid(settings);
            bool outEnabledWanted = outEnabled;
            DateTime outStartWanted = outStart;
            DateTime outStopWanted = outStop;
            int outBatteryLimitPercentWanted = outBatteryLimitPercent;

            if (p == null || p.Action == null)
            {
                outEnabledWanted = false;
            }
            else if(p.Action.DischargeToGrid < 100)
            {
                outEnabledWanted = true;
                outBatteryLimitPercentWanted = p.Action.DischargeToGrid;
                if ( outStart > p.Start)
                {
                    outStartWanted = p.Start;
                }

                // Find the end.
                HalfHourPlan? q = p;
                outStopWanted = p.Start.AddMinutes(30);
                while(q != null && (q?.Action?.DischargeToGrid ?? 100) < 100)
                {
                    q = plan?.GetNext(p);
                    outStopWanted = q?.Start ?? outStopWanted.AddMinutes(30);
                }
            }

            if(outEnabled && !outEnabledWanted)
            {
                await _Lux.SetDishargeToGridLevelAsync(100);
                actions.AppendLine($"SetDishargeToGridLevelAsync(100) to disable was {outBatteryLimitPercent} (enabled: {outEnabled}).");
            }

            if (outStart > outStartWanted )
            {
                await _Lux.SetDishargeToGridStartAsync(outStartWanted);
                actions.AppendLine($"SetDishargeToGridStartAsync({outStartWanted.ToString("HH:mm")}) was {outStart.ToString("HH:mm")}.");
            }

            if ( outStop < outStopWanted)
            {
                await _Lux.SetDishargeToGridStopAsync(outStopWanted);
                actions.AppendLine($"SetDishargeToGridStopAsync({outStopWanted.ToString("HH:mm")}0) was {outStop.ToString("HH:mm")}.");
            }

            if (outEnabledWanted && outBatteryLimitPercentWanted < 100 && outBatteryLimitPercent != outBatteryLimitPercentWanted)
            {
                await _Lux.SetDishargeToGridLevelAsync(outBatteryLimitPercentWanted);
                actions.AppendLine($"SetDishargeToGridLevelAsync({outBatteryLimitPercentWanted}) was {outBatteryLimitPercent} (enabled: {outEnabledWanted}).");
            }

            // Charge from grid.
            (bool inEnabled, DateTime inStart, DateTime inStop, int inBatteryLimitPercent) = _Lux.GetChargeFromGrid(settings);
            bool inEnabledWanted = inEnabled;
            DateTime inStartWanted = inStart;
            DateTime inStopWanted = inStop;
            int inBatteryLimitPercentWanted = inBatteryLimitPercent;
            if (p == null || p.Action == null)
            {
                inEnabledWanted = false;
            }
            else if( p.Action.ChargeFromGrid > 0)
            {
                inEnabledWanted = true;
                inBatteryLimitPercentWanted = p.Action.ChargeFromGrid;
                if (inStart > p.Start)
                {
                    inStartWanted = p.Start;
                }

                // Find the end.
                HalfHourPlan? q = p;
                inStopWanted = p.Start.AddMinutes(30);
                while (q != null && (q?.Action?.ChargeFromGrid ?? 0) > 0)
                {
                    q = plan?.GetNext(p);
                    inStopWanted = q?.Start ?? inStopWanted.AddMinutes(30);
                }
            }

            if (inEnabled && !inEnabledWanted)
            {
                await _Lux.SetChargeFromGridLevelAsync(0);
                actions.AppendLine($"SetChargeFromGridLevelAsync(0) to disable was {inBatteryLimitPercent} (enabled: {inEnabled}).");
            }

            if (inStart > inStartWanted)
            {
                await _Lux.SetChargeFromGridStartAsync(inStartWanted);
                actions.AppendLine($"SetChargeFromGridStartAsync({inStartWanted.ToString("HH:mm")}) was {inStart.ToString("HH:mm")}.");
            }

            if (inStop < inStopWanted)
            {
                await _Lux.SetChargeFromGridStopAsync(inStopWanted);
                actions.AppendLine($"SetChargeFromGridStopAsync({inStopWanted.ToString("HH:mm")}0) was {inStop.ToString("HH:mm")}.");
            }

            if (inEnabledWanted && inBatteryLimitPercentWanted > 0 && inBatteryLimitPercent != inBatteryLimitPercentWanted)
            {
                await _Lux.SetChargeFromGridLevelAsync(inBatteryLimitPercentWanted);
                actions.AppendLine($"SetChargeFromGridLevelAsync({inBatteryLimitPercentWanted}) was {inBatteryLimitPercent} (enabled: {inEnabledWanted}).");
            }


            // Batt.
            int requiredBattChargeRate = 95; // Correct for charge from grid.
            string why = "charge from grid";
            if(!inEnabled)
            {
                if ((p?.Action?.BatteryChargeRate ?? 100) < 100)
                {
                    requiredBattChargeRate = p?.Action?.BatteryChargeRate ?? 100;
                    why = "export generation";
                }
                else
                {
                    int battLevel = await _InfluxQuery.GetBatteryLevelAsync();
                    if (battLevel > 95)
                    {
                        requiredBattChargeRate = 0;
                        why = "battery is full";
                    }
                    else
                    {
                        why = "batttery has space";
                    }
                }
            }

            if( requiredBattChargeRate != battChargeRate)
            {
                await _Lux.SetBatteryChargeRateAsync(requiredBattChargeRate);
                actions.AppendLine($"SetBatteryChargeRate({requiredBattChargeRate}) was {battChargeRate}. Why: {why}.");
            }

            if (actions.Length > 0)
            {
                actions.AppendLine();
                HalfHourPlan? pp = plan?.Current;
                while(pp != null)
                {
                    actions.AppendLine(pp.ToString());
                }
                _Email.SendEmail($"PlanChecker {DateTime.UtcNow.ToString("dd MMM HH:mm")}", actions.ToString());
            }
        }
    }
}
