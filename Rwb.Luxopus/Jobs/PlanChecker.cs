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
                return;
            }

            StringBuilder actions = new StringBuilder();

            HalfHourPlan p = plan.Current;

            if (p.Action == null)
            {
                return;
            }

            // Check that it's doing what it's supposed to be doing.
            // update settings and log warning in case of discrepancy.

            // Are we on target?
            // If not then what can we do about it?

            Dictionary<string, string> settings = await _Lux.GetSettingsAsync();
            int battChargeRate = _Lux.GetBatteryChargeRate(settings);


            (bool outEnabled, DateTime outStart, DateTime outStop, int outBatteryLimitPercent) = _Lux.GetDishargeToGrid(settings);
            if (p.Action.DischargeToGrid < 100)
            {
                DateTime dischargeEnd = p.Start.AddMinutes(30);
                HalfHourPlan? q = plan.GetNext(p);
                while (q != null && (q.Action?.DischargeToGrid ?? 100) < 100)
                {
                    dischargeEnd = dischargeEnd.AddMinutes(30);
                    q = plan.GetNext(q);
                }

                if (!outEnabled || outStart > p.Start || outStop < dischargeEnd || outBatteryLimitPercent != p.Action.DischargeToGrid)
                {
                    //await _Lux.SetDishargeToGridAsync(p.Start, p.Start.AddMinutes(30), p.Action.DischargeToGrid);
                    actions.AppendLine($"SetDishargeToGridAsync({p.Start.ToString("HH:mm")},{dischargeEnd.ToString("HH:mm")}, {p.Action.DischargeToGrid}) was {outEnabled} {outStart.ToString("HH:mm")}, {outStop.ToString("HH:mm")}, {outBatteryLimitPercent}% Sell price is {p.Sell.ToString("00.0")}");
                }
            }
            else if (outEnabled && (p.Start <= outStop || p.Start.AddMinutes(30) > outStart))
            {
                //await _Lux.SetDishargeToGridAsync(p.Start, p.Start, 100);
                actions.AppendLine($"SetDishargeToGridAsync({p.Start.ToString("HH:mm")},{p.Start.ToString("HH:mm")}, 100) was {outEnabled} {outStart.ToString("HH:mm")}, {outStop.ToString("HH:mm")}, {outBatteryLimitPercent}% Sell price is {p.Sell.ToString("00.0")}");
            }

            (bool inEnabled, DateTime inStart, DateTime inStop, int inBatteryLimitPercent) = _Lux.GetChargeFromGrid(settings);
            if (p.Action.ChargeFromGrid > 0)
            {
                DateTime chargeEnd = p.Start.AddMinutes(30);
                HalfHourPlan? q = plan.GetNext(p);
                while (q != null && (q.Action?.ChargeFromGrid ?? 0) > 0)
                {
                    chargeEnd = chargeEnd.AddMinutes(30);
                    q = plan.GetNext(q);
                }

                if (!inEnabled || inStart > p.Start || inStop < chargeEnd || inBatteryLimitPercent != p.Action.ChargeFromGrid)
                {
                    //await _Lux.SetChargeFromGridAsync(p.Start, p.Start.AddMinutes(30), p.Action.ChargeFromGrid);
                    actions.AppendLine($"SetChargeFromGridAsync({p.Start.ToString("HH:mm")}, {chargeEnd.ToString("HH:mm")}, {p.Action.DischargeToGrid}) was {inEnabled} {inStart.ToString("HH:mm")}, {inStop.ToString("HH:mm")}, {inBatteryLimitPercent}% Buy price is {p.Buy.ToString("00.0")}");
                }
            }
            else if (inEnabled && (p.Start <= inStop || p.Start.AddMinutes(30) > inStop))
            {
                //await _Lux.SetChargeFromGridAsync(p.Start, p.Start.AddMinutes(30), 0);
                actions.AppendLine($"SetChargeFromGridAsync({p.Start.ToString("HH:mm")}, {p.Start.ToString("HH:mm")}, 0) was {inEnabled} {inStart.ToString("HH:mm")}, {inStop.ToString("HH:mm")}, {inBatteryLimitPercent}%  Buy price is {p.Buy.ToString("00.0")}");
            }

            if (p.Action.ExportGeneration && battChargeRate > 5)
            {
                //await _Lux.SetBatteryChargeRate(1);
                actions.AppendLine($"SetBatteryChargeRate(1) was {battChargeRate} but Action.ExportGeneration is true.");
            }
            else if (!p.Action.ExportGeneration)
            {
                int battLevel = await _InfluxQuery.GetBatteryLevelAsync();
                if (battLevel < 90 && battChargeRate < 90)
                {
                    // Charge the battery.
                    //await _Lux.SetBatteryChargeRate(90);
                    actions.AppendLine($"SetBatteryChargeRate(90) was {battChargeRate} (battery level is {battLevel})");
                }
            }


            string message = actions.ToString();
            if (!string.IsNullOrEmpty(message))
            {
                _Email.SendEmail($"PlanChecker {DateTime.UtcNow.ToString("dd MMM HH:mm")}", message);
            }
        }
    }
}
