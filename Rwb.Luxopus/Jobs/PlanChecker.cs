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

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            //DateTime t0 = new DateTime(2023, 03, 31, 18, 00, 00);
            DateTime t0 = DateTime.UtcNow;
            IEnumerable<Plan> ps = _Plans.LoadAll(t0);

            Plan? plan = _Plans.Load(DateTime.UtcNow);
            if (plan == null)
            {
                Logger.LogError($"No current plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
                // If there is plan then default configuration will be set.
            }

            DateTime tStart = plan?.Current?.Start ?? DateTime.UtcNow;
            DateTime tNext = plan?.Next?.Start ?? tStart.AddMinutes(30);

            StringBuilder actions = new StringBuilder();

            HalfHourPlan? p = plan?.Current;

            // Check that it's doing what it's supposed to be doing.
            // update settings and log warning in case of discrepancy.

            // Are we on target?
            // If not then what can we do about it?

            Dictionary<string, string> settings = await _Lux.GetSettingsAsync();
            if( settings == null)
            {
                Logger.LogWarning("Could not read LUX settings; abandoning checks.");
                return;
            }
            int battChargeRate = _Lux.GetBatteryChargeRate(settings);

            // Discharge to grid.
            (bool outEnabled, DateTime outStart, DateTime outStop, int outBatteryLimitPercent) = _Lux.GetDishargeToGrid(settings);
            if (p == null || p.Action == null)
            {
                if (outEnabled)
                {
                    await _Lux.SetDishargeToGridAsync(tStart, tNext, 100);
                    actions.AppendLine($"SetDishargeToGridAsync({tStart.ToString("HH:mm")},{tNext.ToString("HH:mm")}, 100) was {outEnabled} {outStart.ToString("HH:mm")}, {outStop.ToString("HH:mm")}, {outBatteryLimitPercent}% No plan.");
                }
            }
            else
            {
                if (p.Action.DischargeToGrid < 100)
                {
                    DateTime dischargeEnd = tNext;
                    HalfHourPlan? q = plan.GetNext(p);
                    while (q != null && (q.Action?.DischargeToGrid ?? 100) < 100)
                    {
                        q = plan.GetNext(q);
                        dischargeEnd = q?.Start ?? dischargeEnd.AddMinutes(30);
                    }

                    if (!outEnabled || outStart > p.Start || outStop < dischargeEnd || outBatteryLimitPercent != p.Action.DischargeToGrid)
                    {
                        // TODO: estimate battery required to get to over night low.
                        await _Lux.SetDishargeToGridAsync(tStart, tNext, 80);// p.Action.DischargeToGrid);
                        actions.AppendLine($"SetDishargeToGridAsync({tStart.ToString("HH:mm")},{dischargeEnd.ToString("HH:mm")}, {p.Action.DischargeToGrid} OVERRIDE 80) was {outEnabled} {outStart.ToString("HH:mm")}, {outStop.ToString("HH:mm")}, {outBatteryLimitPercent}% Sell price is {p.Sell.ToString("00.0")}");
                    }
                }
                else if (outEnabled && (tStart <= outStop || tNext > outStart))
                {
                    await _Lux.SetDishargeToGridAsync(tStart, tStart, 100);
                    actions.AppendLine($"SetDishargeToGridAsync({tStart.ToString("HH:mm")},{tStart.ToString("HH:mm")}, 100) was {outEnabled} {outStart.ToString("HH:mm")}, {outStop.ToString("HH:mm")}, {outBatteryLimitPercent}% Sell price is {p.Sell.ToString("00.0")}");
                }
            }

            // Charge from grid.
            (bool inEnabled, DateTime inStart, DateTime inStop, int inBatteryLimitPercent) = _Lux.GetChargeFromGrid(settings);
            if (p == null || p.Action == null)
            {
                if (inEnabled)
                {
                    await _Lux.SetChargeFromGridAsync(tStart, tNext, 0);
                    actions.AppendLine($"SetChargeFromGridAsync({tStart.ToString("HH:mm")}, {tNext.ToString("HH:mm")}, 0) was {inEnabled} {inStart.ToString("HH:mm")}, {inStop.ToString("HH:mm")}, {inBatteryLimitPercent}% No plan");
                }
            }
            else
            {
                if (p.Action.ChargeFromGrid > 0)
                {
                    DateTime chargeEnd = tNext;
                    HalfHourPlan? q = plan.GetNext(p);
                    while (q != null && (q.Action?.ChargeFromGrid ?? 0) > 0)
                    {
                        q = plan.GetNext(q);
                        chargeEnd =  q?.Start ?? chargeEnd.AddMinutes(30);
                    }

                    if (!inEnabled && ( inStart > tStart || inStop < chargeEnd || inBatteryLimitPercent != p.Action.ChargeFromGrid))
                    {
                        //await _Lux.SetChargeFromGridAsync(p.Start, p.Start.AddMinutes(30), p.Action.ChargeFromGrid);
                        actions.AppendLine($"DISABLED SetChargeFromGridAsync({tStart.ToString("HH:mm")}, {chargeEnd.ToString("HH:mm")}, {p.Action.DischargeToGrid}) was {inEnabled} {inStart.ToString("HH:mm")}, {inStop.ToString("HH:mm")}, {inBatteryLimitPercent}% Buy price is {p.Buy.ToString("00.0")}");
                    }
                }
                else if (inEnabled && (tStart <= inStop || tNext > inStop))
                {
                    //await _Lux.SetChargeFromGridAsync(tStart, tNext.AddMinutes(30), 0);
                    actions.AppendLine($"SetChargeFromGridAsync({tStart.ToString("HH:mm")}, {tNext.ToString("HH:mm")}, 0) was {inEnabled} {inStart.ToString("HH:mm")}, {inStop.ToString("HH:mm")}, {inBatteryLimitPercent}%  Buy price is {p.Buy.ToString("00.0")}");
                }
            }

            // Batt.
            int requiredBattChargeRate = 90; // Correct for charge from grid.
            string why = "charge from grid";
            if(!inEnabled)
            {
                if (p?.Action?.ExportGeneration ?? false)
                {
                    requiredBattChargeRate = 0;
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
                await _Lux.SetBatteryChargeRate(requiredBattChargeRate);
                actions.AppendLine($"SetBatteryChargeRate({requiredBattChargeRate}) was {battChargeRate}. Why: {why}.");
            }

            string message = actions.ToString();
            if (!string.IsNullOrEmpty(message))
            {
                _Email.SendEmail($"PlanChecker {DateTime.UtcNow.ToString("dd MMM HH:mm")}", message);
            }
        }
    }
}
